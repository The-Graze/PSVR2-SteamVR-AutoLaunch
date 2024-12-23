using PSVR2_AutoLaunch.Properties;
using System;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    //mutex to ensure one instance cos I kept opening lots lol
    private static readonly string MutexName = "Global\\PSVR2-SteamVR-AutoLaunchMutex";

    [STAThread]
    static void Main()
    {
        using (Mutex mutex = new Mutex(true, MutexName, out bool isNewInstance))
        {
            if (!isNewInstance)
            {
                MessageBox.Show("PSVR2-SteamVR-AutoLaunch is already running,\nIm only needed once!", "Allready Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Application.Exit();
                return;
            }
            else
            {
                Application.Run(new PSVR2SteamVRAutoLaunch());
            }
        }
    }
}

public class PSVR2SteamVRAutoLaunch : ApplicationContext
{
    private NotifyIcon trayIcon;
    private ManagementEventWatcher insertWatcher;

    public PSVR2SteamVRAutoLaunch()
    {
        //yippe yippee its starting
        trayIcon = new NotifyIcon
        {
            Icon = Resources.AppIcon,
            ContextMenu = new ContextMenu(new MenuItem[]
            {
                new MenuItem("Launch SteamVR Manually", LaunchSteamVR),
                new MenuItem("Exit", Exit)
            }),
            Visible = true
        };

        StartWatcher();
    }

    //this is from google i duuno how works but it dose
    private void StartWatcher()
    {
        WqlEventQuery insertQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
        insertWatcher = new ManagementEventWatcher(insertQuery);
        insertWatcher.EventArrived += new EventArrivedEventHandler(DeviceInserted);
        insertWatcher.Start();
    }

    private void DeviceInserted(object sender, EventArrivedEventArgs e)
    {
        //"last" device connected so better
        if (GetDeviceName(e) == "PS VR2 Data 9")
        {
            LaunchSteamVR(null, null);
        }
    }

    //uses cmd to launch steamVR so its seprate from this app
    private void LaunchSteamVR(object sender, EventArgs e)
    {
        try
        {
            string command = "/C start steam://run/250820";
            ProcessStartInfo processInfo = new ProcessStartInfo("cmd.exe", command)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(processInfo);
        }
        catch (Exception ex)
        {
            //shouldnt happen cos steam will ask you to install it but good to have
            MessageBox.Show($"Failed to launch SteamVR: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    //I dunno how this works i got it from google lol
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
            return "Error getting device name";
        }

        return "Unknown Device";
    }
    //quitting im quitting!
    private void Exit(object sender, EventArgs e)
    {
        trayIcon.Visible = false;
        trayIcon.Dispose();

        if (insertWatcher != null)
        {
            insertWatcher.Stop();
            insertWatcher.Dispose();
        }
        Application.Exit();
    }
}
