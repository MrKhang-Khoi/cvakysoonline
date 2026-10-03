using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VGCASignToolPro.Services
{
    /// <summary>
    /// Dịch vụ lắng nghe và đồng bộ dữ liệu thời gian thực (Real-time Watcher)
    /// Kết nối luồng Server-Sent Events (SSE) tới Firebase Realtime Database
    /// Hoạt động 100% trên luồng ngầm, tiêu thụ CPU xấp xỉ 0%, RAM &lt; 2MB
    /// </summary>
    public class WorkflowRealtimeWatcher : IDisposable
    {
        private readonly ReportWorkflowService _workflowService;
        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _cts;
        private Task? _watcherTask;
        private Task? _heartbeatTask;
        private string _myCccd = string.Empty;
        private bool _isDisposed;
        private DateTime _lastEventTime = DateTime.MinValue;
        private readonly object _syncLock = new();

        public event Action? OnDataChanged;
        public event Action<string, string, string>? OnReportReturned;
        public event Action<string, string, string>? OnNewReportToApprove;

        public bool IsRunning { get; private set; }

        public WorkflowRealtimeWatcher(ReportWorkflowService workflowService)
        {
            _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
            _httpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan // Kết nối streaming liên tục dài hạn
            };
            _httpClient.DefaultRequestHeaders.Add("Accept", "text/event-stream");
        }

        public void Start(string myCccd)
        {
            lock (_syncLock)
            {
                if (IsRunning && string.Equals(_myCccd, myCccd, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Stop();

                _myCccd = myCccd ?? string.Empty;
                _cts = new CancellationTokenSource();
                IsRunning = true;

                var token = _cts.Token;
                _watcherTask = Task.Run(() => RunSseStreamingLoopAsync(token), token);
                _heartbeatTask = Task.Run(() => RunAdaptiveHeartbeatLoopAsync(token), token);

                AppLogger.Info($"[WorkflowRealtimeWatcher] Bắt đầu lắng nghe thời gian thực cho CCCD '{_myCccd}'", "RealtimeWatcher");
            }
        }

        public void Stop()
        {
            lock (_syncLock)
            {
                if (!IsRunning) return;

                IsRunning = false;
                try
                {
                    _cts?.Cancel();
                    _cts?.Dispose();
                }
                catch { }
                _cts = null;
                _watcherTask = null;
                _heartbeatTask = null;

                AppLogger.Info("[WorkflowRealtimeWatcher] Đã dừng lắng nghe thời gian thực.", "RealtimeWatcher");
            }
        }

        private async Task RunSseStreamingLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string firebaseUrl = _workflowService.FirebaseUrl;
                    string secret = _workflowService.Secret;

                    if (string.IsNullOrWhiteSpace(firebaseUrl))
                    {
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    string endpoint = $"{firebaseUrl}/reports.json" +
                        (!string.IsNullOrEmpty(secret) ? $"?auth={secret}" : "");

                    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    request.Headers.Add("Accept", "text/event-stream");

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        AppLogger.Info($"[RealtimeWatcher] Máy chủ Firebase phản hồi status {(int)response.StatusCode}, thử kết nối lại sau 5s...", "RealtimeWatcher");
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    string currentEvent = "message";

                    while (!ct.IsCancellationRequested && !reader.EndOfStream)
                    {
                        string? line = await reader.ReadLineAsync(ct);
                        if (line == null) break;

                        line = line.Trim();
                        if (line.StartsWith("event:"))
                        {
                            currentEvent = line.Substring(6).Trim();
                        }
                        else if (line.StartsWith("data:"))
                        {
                            string dataJson = line.Substring(5).Trim();
                            _lastEventTime = DateTime.UtcNow; // Bug #10 fix: đã move lên trước if keep-alive

                            if (currentEvent == "keep-alive" || dataJson == "null")
                            {
                                // _lastEventTime đã được reset ở trên — heartbeat sẽ không trigger sai
                                continue;
                            }

                            // Xử lý sự kiện cập nhật dữ liệu (put hoặc patch)
                            ProcessRealtimeDataEvent(currentEvent, dataJson);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        System.Diagnostics.Trace.TraceWarning($"[RealtimeWatcher] Kết nối SSE tạm gián đoạn: {ex.Message}. Tự động kết nối lại sau 4s...");
                        try { await Task.Delay(4000, ct); } catch { break; }
                    }
                }
            }
        }

        /// <summary>
        /// Bộ nhịp tim thích ứng chạy ngầm (Phao cứu sinh đề phòng trường học dùng Proxy chặn SSE Stream)
        /// </summary>
        private async Task RunAdaptiveHeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Chờ chu kỳ 15 giây
                    await Task.Delay(15000, ct);

                    // Nếu trong 30 giây vừa qua không nhận được sự kiện SSE nào, thực hiện kiểm tra ngầm
                    if (DateTime.UtcNow - _lastEventTime > TimeSpan.FromSeconds(30))
                    {
                        NotifyDataChangedDebounced();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Bỏ qua lỗi heartbeat để không ngắt luồng
                }
            }
        }

        private long _lastDebounceTicks = 0;

        private void ProcessRealtimeDataEvent(string eventType, string dataJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dataJson) || dataJson == "null") return;

                JToken token = JToken.Parse(dataJson);
                JToken? dataObj = token["data"] ?? token;

                // Kiểm tra xem sự kiện có chứa thông tin trả về báo cáo hoặc phê duyệt liên quan đến giáo viên hiện tại không
                if (dataObj is JObject jobj)
                {
                    // Trường hợp patch 1 item hoặc put toàn bộ danh sách
                    CheckAndNotifySpecificEvents(jobj);
                }

                NotifyDataChangedDebounced();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[RealtimeWatcher] Lỗi phân tích gói dữ liệu thời gian thực: {ex.Message}");
                NotifyDataChangedDebounced();
            }
        }

        private void CheckAndNotifySpecificEvents(JObject obj)
        {
            if (string.IsNullOrEmpty(_myCccd)) return;

            // Kiểm tra xem đối tượng có cấu trúc của 1 báo cáo đơn lẻ không
            if (obj.ContainsKey("status") && obj.ContainsKey("reportId"))
            {
                InspectSingleReportNode(obj);
                return;
            }

            // Hoặc là một dictionary chứa nhiều báo cáo
            foreach (var prop in obj.Properties())
            {
                if (prop.Value is JObject childObj && childObj.ContainsKey("status"))
                {
                    InspectSingleReportNode(childObj);
                }
            }
        }

        private void InspectSingleReportNode(JObject reportObj)
        {
            string status = reportObj["status"]?.ToString() ?? string.Empty;
            string senderCccd = reportObj["senderCccd"]?.ToString() ?? string.Empty;
            string approverCccd = reportObj["approverCccd"]?.ToString() ?? string.Empty;
            string reportId = reportObj["reportId"]?.ToString() ?? string.Empty;
            string title = reportObj["title"]?.ToString() ?? "Báo cáo";
            string reason = reportObj["returnReason"]?.ToString() ?? string.Empty;
            string senderName = reportObj["senderName"]?.ToString() ?? string.Empty;

            if (status == "RETURNED" && string.Equals(senderCccd, _myCccd, StringComparison.OrdinalIgnoreCase))
            {
                OnReportReturned?.Invoke(reportId, title, reason);
            }
            else if (status == "WAITING_OPEN" && string.Equals(approverCccd, _myCccd, StringComparison.OrdinalIgnoreCase))
            {
                OnNewReportToApprove?.Invoke(reportId, title, senderName);
            }
        }

        private void NotifyDataChangedDebounced()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastDebounceTicks);

            // Debounce 300 mili-giây để gom các sự kiện patch liên tiếp thành 1 lần refresh duy nhất
            if (now - last > TimeSpan.FromMilliseconds(300).Ticks)
            {
                Interlocked.Exchange(ref _lastDebounceTicks, now);
                try
                {
                    OnDataChanged?.Invoke();
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            _httpClient.Dispose();
        }
    }
}
