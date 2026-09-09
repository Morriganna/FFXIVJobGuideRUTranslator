using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FFXIVJobGuideRUTranslator.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string newAddonName = string.Empty;

    public ConfigWindow(Plugin plugin) : base("FFXIV JobGuide RU Translator###JobGuideRUConfig")
    {
        Size = new Vector2(420, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var configuration = plugin.Configuration;

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Плагин включён", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        var translateTooltip = configuration.TranslateHoverTooltip;
        if (ImGui.Checkbox("Переводить всплывающую подсказку умения на хотбаре", ref translateTooltip))
        {
            configuration.TranslateHoverTooltip = translateTooltip;
            configuration.Save();
        }

        var translateActionMenu = configuration.TranslateActionMenu;
        if (ImGui.Checkbox("Переводить панель описания в окне \"Actions & Traits\"", ref translateActionMenu))
        {
            configuration.TranslateActionMenu = translateActionMenu;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Список аддонов, где разрешена подмена текста:");
        ImGui.TextWrapped("Проверьте актуальные имена через /xldata -> Addon Inspector, если что-то не работает.");

        string? toRemove = null;
        foreach (var name in configuration.TargetAddonNames)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Убрать###remove_{name}"))
                toRemove = name;
        }

        if (toRemove is not null)
        {
            configuration.TargetAddonNames.Remove(toRemove);
            configuration.Save();
            plugin.ApplyAddonRegistrations();
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##newAddonName", ref newAddonName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Добавить аддон") && !string.IsNullOrWhiteSpace(newAddonName))
        {
            if (!configuration.TargetAddonNames.Contains(newAddonName))
            {
                configuration.TargetAddonNames.Add(newAddonName);
                configuration.Save();
                plugin.ApplyAddonRegistrations();
            }

            newAddonName = string.Empty;
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Загружено записей перевода: {plugin.Repository.TotalParsed}");
        ImGui.TextUnformatted($"Сопоставлено с ID умений игры: {plugin.Repository.TotalResolved}");
        ImGui.TextUnformatted(configuration.LastUpdateUtc is { } last
            ? $"Последнее обновление с GitHub: {last.ToLocalTime():g}"
            : "Перевод: встроенный в плагин бандл (обновление с GitHub ещё не запускалось)");

        ImGui.Separator();

        if (plugin.IsUpdating)
        {
            ImGui.TextUnformatted("Обновляю перевод с GitHub...");
        }
        else
        {
            if (ImGui.Button("Обновить перевод с GitHub"))
                plugin.UpdateTranslationsAsync();

            ImGui.SameLine();
            if (ImGui.Button("Перезагрузить встроенный бандл"))
                plugin.ReloadBundledTranslations();
        }

        if (!string.IsNullOrEmpty(plugin.LastUpdateStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(plugin.LastUpdateStatus);
        }
    }
}
