using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ImSharp;
using Luna;
using Penumbra.Communication;
using Penumbra.Services;
using Penumbra.UI.Classes;
using TabType = Penumbra.Api.Enums.TabType;
using Window = Luna.Window;

namespace Penumbra.UI.MainWindow;

public sealed class MainWindow : Window
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration           _config;
    private readonly ValidityChecker         _validityChecker;
    private readonly GlobalModImporter       _globalModImporter;
    private readonly UiNavigator             _navigator;
    private          Penumbra?               _penumbra;
    private          MainTabBar              _configTabs = null!;
    private          string?                 _lastException;

    public MainWindow(IDalamudPluginInterface pi, Configuration config, ValidityChecker checker,
        TutorialService tutorial, GlobalModImporter globalModImporter, UiNavigator navigator)
        : base(checker.GetMainWindowLabel())
    {
        _pluginInterface   = pi;
        _config            = config;
        _validityChecker   = checker;
        _globalModImporter = globalModImporter;
        _navigator         = navigator;

        _navigator.ToggleMainWindow += OnToggleMainWindow;
        RespectCloseHotkey          =  true;
        tutorial.UpdateTutorialStep();
        IsOpen = _config.OpenWindowAtStart;
    }

    public void OpenSettings()
    {
        _configTabs.NextTab = TabType.Settings;
        IsOpen              = true;
    }

    public void Setup(Penumbra penumbra, MainTabBar configTabs)
    {
        _penumbra           = penumbra;
        _configTabs         = configTabs;
        _configTabs.NextTab = _config.Ephemeral.SelectedTab;
    }

    public override bool DrawConditions()
        => _penumbra != null;

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = _config.MinimumSize,
            MaximumSize = new Vector2(4096, 2160),
        };
    }

    public override void Draw()
    {
        UiHelpers.SetupCommonSizes();
        _globalModImporter.DrawWindowTarget();
        try
        {
            _configTabs.Draw();
            _lastException = null;
        }
        catch (Exception e)
        {
            if (_lastException != null)
            {
                var text = e.ToString();
                if (text == _lastException)
                    return;

                _lastException = text;
            }
            else
            {
                _lastException = e.ToString();
            }

            Penumbra.Log.Error($"Exception thrown during UI Render:\n{_lastException}");
        }
    }

   

    private void DrawProblemWindow(Utf8StringHandler<TextStringHandlerBuffer> text)
    {
        using var color = ImGuiColor.Text.Push(Colors.RegexWarningBorder);
        Im.Line.New();
        Im.Line.New();
        Im.TextWrapped(ref text);
        color.Pop();

        Im.Line.New();
        Im.Line.New();
        SupportButton.Discord(Penumbra.Messager, 0);
        Im.Line.Same();
        UiHelpers.DrawSupportButton(_penumbra!);
        Im.Line.New();
        Im.Line.New();
    }

    private void OnToggleMainWindow(bool open)
        => IsOpen = open;
}
