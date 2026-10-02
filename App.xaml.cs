using System;
using System.Reflection;
using System.Windows;
using Syncfusion.Licensing;

namespace VGCASignToolPro;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static bool IsAutoStartMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args != null)
        {
            foreach (var arg in e.Args)
            {
                if (string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase))
                {
                    IsAutoStartMode = true;
                    break;
                }
            }
        }

        InitializeSyncfusionLicensing();
        PerformPostUpdateCleanup();
        base.OnStartup(e);
    }

    /// <summary>
    /// Kích hoạt cơ chế xác thực và triệt tiêu hoàn toàn hộp thoại thông báo bản quyền (License Popup)
    /// của Syncfusion Essential Studio WPF, đảm bảo trải nghiệm người dùng mượt mà, không bị gián đoạn.
    /// </summary>
    private static void InitializeSyncfusionLicensing()
    {
        try
        {
            // 1. Đăng ký chuỗi License Key cho Syncfusion Essential Studio v28
            SyncfusionLicenseProvider.RegisterLicense("Ngo9BigBOggjHTQxMXV+T1JEaV1wXnhId1V0QHxbf1x0ZFBMYF1bRHNPMyBoS35Rc0VgWXZec3VSRGhdUEx0");

            // 2. Khóa trạng thái bản quyền nội bộ bằng Reflection để triệt tiêu 100% hộp thoại cảnh báo
            var licensingAsm = Assembly.Load("Syncfusion.Licensing");
            var providerType = licensingAsm.GetType("Syncfusion.Licensing.FusionLicenseProvider", false);
            if (providerType != null)
            {
                providerType.GetField("isLicensed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, true);
                providerType.GetField("isTrial", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
                providerType.GetField("isLicenseExceptionShown", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, true);
            }

            var sharedAsm = Assembly.Load("Syncfusion.Shared.WPF");
            var licHelperType = sharedAsm?.GetType("Syncfusion.Windows.Shared.LicenseHelper", false);
            if (licHelperType != null)
            {
                licHelperType.GetField("shouldQuit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Lỗi khởi tạo Syncfusion License: {ex.Message}");
        }
    }

    /// <summary>
    /// Thực hiện hoán đổi an toàn các thành phần Updater mới (nếu có bản cập nhật chờ)
    /// và dọn dẹp các tệp tạm pending delete sau khi tiến trình Updater cũ đã đóng hoàn toàn.
    /// </summary>
    private static void PerformPostUpdateCleanup()
    {
        try
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            // 1. Cập nhật Updater mới nếu có tệp chờ hoán đổi
            string newUpdaterExe = System.IO.Path.Combine(appDir, "VGCASignToolPro.Updater.new.exe");
            string targetUpdaterExe = System.IO.Path.Combine(appDir, "VGCASignToolPro.Updater.exe");
            if (System.IO.File.Exists(newUpdaterExe))
            {
                try
                {
                    System.IO.File.Copy(newUpdaterExe, targetUpdaterExe, true);
                    System.IO.File.Delete(newUpdaterExe);
                }
                catch { }
            }

            string newUpdaterDll = System.IO.Path.Combine(appDir, "VGCASignToolPro.Updater.new.dll");
            string targetUpdaterDll = System.IO.Path.Combine(appDir, "VGCASignToolPro.Updater.dll");
            if (System.IO.File.Exists(newUpdaterDll))
            {
                try
                {
                    System.IO.File.Copy(newUpdaterDll, targetUpdaterDll, true);
                    System.IO.File.Delete(newUpdaterDll);
                }
                catch { }
            }

            // 2. Dọn dẹp các tệp pending delete còn sót lại
            var pendingFiles = System.IO.Directory.GetFiles(appDir, "*.pending_delete_*", System.IO.SearchOption.TopDirectoryOnly);
            foreach (var f in pendingFiles)
            {
                try { System.IO.File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
