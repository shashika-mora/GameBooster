using Microsoft.UI.Xaml;

namespace GameBooster;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ => _ = new App());
    }
}
