using PSVR2_AutoLaunch;
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
    private ManagementEventWatcher removeWatcher;
    private SenseAutoRepair senseAutoRepair;
    private MenuItem senseAutoRepairToggle;

    public PSVR2SteamVRAutoLaunch()
    {
        //yippe yippee its starting
        trayIcon = new NotifyIcon
        {
            Icon = Resources.AppIcon,
            Visible = true
        };

        senseAutoRepair = new SenseAutoRepair(trayIcon);

        senseAutoRepairToggle = new MenuItem("Auto-repair Sense controllers")
        {
            Checked = senseAutoRepair.Enabled
        };
        senseAutoRepairToggle.Click += (s, a) =>
        {
            senseAutoRepairToggle.Checked = !senseAutoRepairToggle.Checked;
            senseAutoRepair.Enabled = senseAutoRepairToggle.Checked;
        };

        trayIcon.ContextMenu = new ContextMenu(new MenuItem[]
        {
            new MenuItem("Launch SteamVR Manually", LaunchSteamVR),
            new MenuItem("-"),
            senseAutoRepairToggle,
            new MenuItem("-"),
            new MenuItem("Exit", Exit)
        });

        StartWatcher();
    }

    //this is from google i duuno how works but it dose
    private void StartWatcher()
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
        string deviceId = GetDeviceId(e);

        //"last" device connected so better
        if (GetDeviceNameById(deviceId) == "PS VR2 Data 9")
        {
            LaunchSteamVR(null, null);
        }

        if (SenseAutoRepair.TryParseSenseSide(deviceId, out SenseSide side))
        {
            senseAutoRepair.OnUsbArrival(side);
        }
    }

    private void DeviceRemoved(object sender, EventArrivedEventArgs e)
    {
        string deviceId = GetDeviceId(e);

        if (SenseAutoRepair.TryParseSenseSide(deviceId, out SenseSide side))
        {
            senseAutoRepair.OnUsbDeparture(side);
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

    private string GetDeviceId(EventArrivedEventArgs e)
    {
        try
        {
            ManagementBaseObject instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            return instance["Dependent"].ToString().Split('=')[1].Trim('"');
        }
        catch (Exception)
        {
            return null;
        }
    }

    //I dunno how this works i got it from google lol
    private string GetDeviceNameById(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return "Unknown Device";
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPEntity WHERE DeviceID = '{deviceId}'"))
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
        senseAutoRepair?.Shutdown();

        trayIcon.Visible = false;
        trayIcon.Dispose();

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
