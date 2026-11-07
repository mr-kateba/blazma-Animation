using Blazma.AI;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Marketing capture (BLAZMA_ONLY_REELMODELS=1): the real AI models window on this machine (the real hardware probe, the models
/// already installed in BLAZMA_REEL_MODELS_DIR) and the real device list of the AI settings. Nothing is simulated.
/// </summary>
public sealed partial class SelfTest
{
    async Task RunReelModels()
    {
        App.SetLanguage("ar"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        await Settle();
        var dir = System.Environment.GetEnvironmentVariable("BLAZMA_REEL_MODELS_DIR") ?? ModelCatalog.DefaultModelsDirectory();
        var s = App.Settings;
        s.KimodoModels = dir; s.KimodoTextModel = "Llama-3-Kimodo-Q4_K_M.gguf"; s.KimodoDevice = "auto";
        var dlg = _w.AiModelsDlg;
        dlg.ModelsDirOverride = dir;
        _w.OpenAiModels();
        await WaitFor(() => dlg.ProbesDone > 0, 30);
        await Settle();
        GD.Print("REEL gpu line: " + dlg.GpuText + " | light installed=" + dlg.Cards["light"].Installed);
        Shot(P("reel-ar-models-installed"), dlg);
        dlg.Hide();
        await Settle();
        _w.OpenAiSettings();
        await Settle();
        Shot(P("reel-ar-ai-settings"), _w.AiSettingsDlg);
        _w.AiSettingsDlg.DeviceBox.ShowPopup();
        await Settle();
        Shot(P("reel-ar-device-popup"), _w.AiSettingsDlg.DeviceBox.GetPopup());
    }
}
