using System.Windows;

namespace TheGarden.Desktop;
public partial class App : Application
{
    // the climate scratch folders: this run's goes when it ends, and those of runs that ended without it at the start
    protected override void OnStartup(StartupEventArgs e) { base.OnStartup(e); WorldClimateJob.CleanScratch(own: false); }
    protected override void OnExit(ExitEventArgs e) { WorldClimateJob.CleanScratch(own: true); base.OnExit(e); }
}
