using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace Stillwave
{
    public partial class App : Application
    {
        public const string PipeName = "Stillwave.commands";
        static Mutex single;

        protected override void OnStartup(StartupEventArgs e)
        {
            // taskbar jump-list entries start a second copy with "--cmd <name>"; hand it to the running one and quit
            string cmd = null;
            for (int i = 0; i + 1 < e.Args.Length; i++) if (e.Args[i] == "--cmd") cmd = e.Args[i + 1];

            single = new Mutex(true, "Stillwave.single", out bool first);
            if (!first)
            {
                try
                {
                    using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    c.Connect(1500);
                    using var w = new StreamWriter(c) { AutoFlush = true };
                    w.WriteLine(cmd ?? "show");
                }
                catch { }
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }
    }
}
