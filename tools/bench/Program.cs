using System.Diagnostics;
using System.Runtime.InteropServices;

// Test apps for measuring Sakin. Each is a real window so Sakin manages it like a user app.
//   SakinBench hog <mb> <name> <log>     holds <mb> of touched private memory; every time its window
//                                         is activated it re-reads all of it and logs how long that took
//   SakinBench spin <threads> <name>      a background app burning <threads> threads
//   SakinBench fg <name> <log>            the app "in front": each activation runs a fixed
//                                         single-thread job and logs its time
static class P
{
    [STAThread]
    static void Main(string[] a)
    {
        Application.EnableVisualStyles();
        var f = new Form { Text = a.Length > 2 ? a[2] : a.Length > 1 ? a[1] : "bench", Width = 360, Height = 160, StartPosition = FormStartPosition.Manual };
        var lbl = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11), TextAlign = ContentAlignment.MiddleCenter };
        f.Controls.Add(lbl);
        switch (a[0])
        {
            case "hog":
            {
                int mb = int.Parse(a[1]); string log = a[3];
                // touched, private, unshared memory, like a real app's working data
                var blocks = new List<byte[]>();
                for (int i = 0; i < mb; i++) { var b = new byte[1 << 20]; for (int j = 0; j < b.Length; j += 4096) b[j] = 1; blocks.Add(b); }
                lbl.Text = $"{a[2]}: holding {mb} MB";
                bool first = true;
                f.Activated += (_, _) =>
                {
                    if (first) { first = false; return; }
                    var sw = Stopwatch.StartNew();
                    long sum = 0;
                    foreach (var b in blocks) for (int j = 0; j < b.Length; j += 4096) sum += b[j];
                    File.AppendAllText(log, $"{a[2]},touch_ms,{sw.Elapsed.TotalMilliseconds:0},{sum}\n");
                };
                break;
            }
            case "spin":
            {
                int n = int.Parse(a[1]);
                lbl.Text = $"{a[2]}: {n} busy threads";
                for (int i = 0; i < n; i++)
                    new Thread(() => { double x = 1; while (true) { x = Math.Sqrt(x + 1.0000001); } }) { IsBackground = true }.Start();
                break;
            }
            case "fg":
            {
                string log = a[2];
                lbl.Text = "foreground job";
                f.Activated += async (_, _) =>
                {
                    await Task.Delay(300);
                    var sw = Stopwatch.StartNew();
                    // fixed single-thread work (~1-2 s on an idle P-core)
                    double x = 1; for (long i = 0; i < 600_000_000; i++) x = x * 1.0000001 + 0.0000001;
                    File.AppendAllText(log, $"fg,job_ms,{sw.Elapsed.TotalMilliseconds:0},{x:0}\n");
                };
                break;
            }
        }
        Application.Run(f);
    }
}
