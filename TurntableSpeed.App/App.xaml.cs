namespace TurntableSpeed.App;

public partial class App : Application
{
	public App()
	{
		// Touching the singleton is what restores the stored language (or picks up the phone's).
		// It has to happen before the shell exists, so the first frame is already in the right
		// language rather than repainting into it.
		_ = Localization.LanguageViewModel.Instance;

		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}