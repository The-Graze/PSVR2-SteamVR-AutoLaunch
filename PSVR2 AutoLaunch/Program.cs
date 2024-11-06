using PSVR2_AutoLaunch.Properties;
using System;
using System.Diagnostics;
using System.Management;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PSVR2TrayLauncher());
    }
}

public class PSVR2TrayLauncher : ApplicationContext
{
    private NotifyIcon trayIcon;
    private ManagementEventWatcher insertWatcher;
    private ManagementEventWatcher removeWatcher;

    public PSVR2TrayLauncher()
    {
        trayIcon = new NotifyIcon()
        {
            Icon = Resources.AppIcon,
            ContextMenu = new ContextMenu(new MenuItem[] {
                new MenuItem("Launch SteamVR Manualy", LaunchSteamVR),
                new MenuItem("Exit", Exit)
            }),
            Visible = true
        };
        StartWatchers();
    }

    private void StartWatchers()
    {
        WqlEventQuery insertQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
        insertWatcher = new ManagementEventWatcher(insertQuery);
        insertWatcher.EventArrived += new EventArrivedEventHandler(DeviceInserted);
        insertWatcher.Start();

        WqlEventQuery removeQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
        removeWatcher = new ManagementEventWatcher(removeQuery);
        removeWatcher.EventArrived += new EventArrivedEventHandler(DeviceRemoved);
        removeWatcher.Start();
    }

    private void DeviceInserted(object sender, EventArrivedEventArgs e)
    {
        string Name = GetDeviceName(e);
        //MessageBox.Show($"device connected: {Name}");
        if (Name == "PS VR2 Data 9") //this is the last device that connects with a "PS" name
        {
            LaunchSteamVR(null,null);
        }
    }

    void LaunchSteamVR(object sender, EventArgs e)
    {
        string command = "/C start steam://run/250820";
        ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", command)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(processInfo);
    }

    private void DeviceRemoved(object sender, EventArrivedEventArgs e)
    {
        //string Name = GetDeviceName(e);
        //MessageBox.Show($"device removed: {deviceName}");

        //may add stuff here who knows
    }

    private string GetDeviceName(EventArrivedEventArgs e)
    {
        try
        {
            ManagementBaseObject instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            string deviceID = instance["Dependent"].ToString().Split('=')[1].Trim('"');
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{deviceID}'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    return obj["Name"]?.ToString() ?? "Unknown Device";
                }
            }
        }
        catch (Exception)
        {
            return "Error retrieving device name";
        }

        return "Unknown Device";
    }

    void Exit(object sender, EventArgs e)
    {
        trayIcon.Visible = false;
        if (insertWatcher != null)
        {
            insertWatcher.Stop();
            insertWatcher.Dispose();
        }
        if (removeWatcher != null)
        {
            removeWatcher.Stop();
            removeWatcher.Dispose();
        }
        Application.Exit();
    }
}