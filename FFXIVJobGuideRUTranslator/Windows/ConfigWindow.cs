using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVJobGuideRUTranslator.Data;

namespace FFXIVJobGuideRUTranslator.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string newAddonName = string.Empty;

    // Состояние вкладки "Debug: умения" - живёт, пока открыто окно, специально не сохраняется.
    private List<JobActionDumpRow> debugRows = new();
    private string? debugJobAbbreviation;
    private bool debugOnlyUnresolved;
    private bool debugHasScanned;

    public ConfigWindow(Plugin plugin) : base("FFXIV JobGuide RU Translator###JobGuideRUConfig")
    {
        Size = new Vector2(480, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##jgru_tabs"))
        {
            if (ImGui.BeginTabItem("Настройки"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Debug: умения"))
            {
                DrawDebugTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawSettingsTab()
    {
        var configuration = plugin.Configuration;

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Плагин включён", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        var useNativeWindow = configuration.UseNativeTranslationWindow;
        if (ImGui.Checkbox("Экспериментально: показывать перевод нативной подсказкой игры вместо ImGui", ref useNativeWindow))
        {
            configuration.UseNativeTranslationWindow = useNativeWindow;
            configuration.Save();
        }
        ImGui.TextWrapped(
            "Прячет родную подсказку умения (только когда для неё есть перевод) и показывает перевод " +
            "через тот же нативный менеджер подсказок, которым игра сама показывает подсказки у иконок " +
            "валют, ссылок в чате и т.п. (AtkTooltipManager) - выглядит жёлтой подсказкой-пузырём, а не " +
            "окном в рамке, как у самого ActionDetail. Если что-то сломалось (в т.ч. в ДРУГИХ окнах, не " +
            "связанных с умениями), выключите этот тумблер здесь.");

        ImGui.Separator();
        ImGui.TextUnformatted("Список аддонов, где разрешена подмена текста:");
        ImGui.TextWrapped("Проверьте актуальные имена через /xldata -> Addon Inspector, если что-то не работает.");

        var toRemoveIndex = -1;
        for (var i = 0; i < configuration.TargetAddonNames.Count; i++)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(configuration.TargetAddonNames[i]);
            ImGui.SameLine();
            // ID кнопки завязан на индекс, а не на имя - иначе одинаковые записи (например, если
            // список случайно продублировался) получают одинаковый ImGui ID и путаются между собой.
            if (ImGui.SmallButton($"Убрать###remove_{i}"))
                toRemoveIndex = i;
        }

        if (toRemoveIndex >= 0)
        {
            configuration.TargetAddonNames.RemoveAt(toRemoveIndex);
            configuration.Save();
            plugin.ApplyAddonRegistrations();
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##newAddonName", ref newAddonName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Добавить аддон"))
        {
            var trimmed = newAddonName.Trim();
            if (!string.IsNullOrEmpty(trimmed) &&
                !configuration.TargetAddonNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                configuration.TargetAddonNames.Add(trimmed);
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

    private void DrawDebugTab()
    {
        ImGui.TextWrapped(
            "Список умений, УНИКАЛЬНЫХ для текущей работы персонажа (вкладки \"Job\" в Actions&Traits " +
            "и в PvP Actions), и статус их сопоставления с переводом. Общие Role-умения (на несколько " +
            "работ одной роли, обычный или PvP) и Quick Chat намеренно не включены. Нужно быть в игре " +
            "персонажем - список строится по вашей активной работе на момент нажатия \"Сканировать\". " +
            "Крафт/сбор (CraftAction) сюда пока не входят.");

        ImGui.Spacing();

        if (ImGui.Button("Сканировать текущую работу"))
        {
            debugRows = plugin.BuildJobActionDump(out debugJobAbbreviation);
            debugHasScanned = true;
        }

        ImGui.SameLine();
        ImGui.Checkbox("Только несопоставленные", ref debugOnlyUnresolved);

        if (!debugHasScanned)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Нажмите \"Сканировать текущую работу\", чтобы получить список.");
            return;
        }

        if (debugJobAbbreviation is null)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Не удалось определить работу - зайдите в игру персонажем и попробуйте снова.");
            return;
        }

        var visibleRows = debugOnlyUnresolved
            ? debugRows.Where(r => !r.IsResolved).ToList()
            : debugRows;

        ImGui.Spacing();
        ImGui.TextUnformatted($"Работа: {debugJobAbbreviation} - всего умений: {debugRows.Count}, сопоставлено: {debugRows.Count(r => r.IsResolved)}, показано: {visibleRows.Count}");

        ImGui.SameLine();
        if (ImGui.SmallButton("Скопировать список в буфер"))
            ImGui.SetClipboardText(BuildClipboardText(visibleRows));

        if (visibleRows.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("Пусто.");
            return;
        }

        // Стандартный API таблиц ImGui.NET (BeginTable/TableSetupColumn/TableHeadersRow/...) -
        // как и остальной код с Dalamud.Bindings.ImGui в этом проекте, не проверялся на живом
        // клиенте (см. README, "Важно понимать заранее"). Если тут ошибка компиляции - смотрите
        // в IntelliSense точные имена ImGuiTableFlags/ImGuiTableColumnFlags в вашей версии биндинга.
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                            ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;

        if (ImGui.BeginTable("##jgru_debug_table", 5, tableFlags, new Vector2(0, 300)))
        {
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Название (EN)", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("Группа", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Статус", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Перевод (превью)", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var row in visibleRows)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.ActionId.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.EnglishName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Group);

                ImGui.TableNextColumn();
                if (row.IsResolved)
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "есть перевод");
                else
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "нет перевода");

                ImGui.TableNextColumn();
                var preview = row.RussianPreview?.Replace('\n', ' ') ?? string.Empty;
                if (preview.Length > 80)
                    preview = preview[..80] + "...";
                ImGui.TextWrapped(preview);
            }

            ImGui.EndTable();
        }
    }

    private static string BuildClipboardText(IReadOnlyList<JobActionDumpRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ActionId\tName\tGroup\tResolved\tTranslation");
        foreach (var row in rows)
        {
            var preview = row.RussianPreview?.Replace('\t', ' ').Replace('\n', ' ') ?? string.Empty;
            sb.AppendLine($"{row.ActionId}\t{row.EnglishName}\t{row.Group}\t{(row.IsResolved ? "yes" : "no")}\t{preview}");
        }

        return sb.ToString();
    }
}
