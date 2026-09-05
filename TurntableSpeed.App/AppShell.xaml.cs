using TurntableSpeed.App.Localization;

namespace TurntableSpeed.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

		LocalizationManager.Instance.LanguageChanged += (_, _) => SyncTabTitles();
		SyncTabTitles();
	}

	/// <summary>
	/// Copies each tab's title up onto the ShellSection that wraps it.
	/// <para>
	/// The titles in the XAML are bound and do follow the language, but they sit on the
	/// ShellContent. A ShellContent placed straight into a TabBar gets an implicit ShellSection
	/// around it, and the Android bottom tab bar is built from the section — which takes the
	/// child's title when the section is created, not afterwards. Pushing the value up again is
	/// what gives the renderer a property change it is watching.
	/// </para>
	/// </summary>
	private void SyncTabTitles()
	{
		foreach (var item in Items)
		{
			foreach (var section in item.Items)
			{
				// Only when the section exists solely to wrap one page; a section with several
				// pages carries a title of its own that is none of this method's business.
				if (section.Items.Count == 1 && section.Items[0].Title is { Length: > 0 } title)
				{
					section.Title = title;
				}
			}
		}
	}
}
