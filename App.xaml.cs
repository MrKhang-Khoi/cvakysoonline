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
}
