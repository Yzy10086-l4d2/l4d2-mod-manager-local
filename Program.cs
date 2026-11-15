using System;
using System.Windows.Forms;

namespace LocalModMgr
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Core.Load();
            Application.Run(new MainForm());
        }
    }
}
