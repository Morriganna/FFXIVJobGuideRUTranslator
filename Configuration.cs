using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FFXIVJobGuideRUTranslator;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Глобальный выключатель — если false, плагин не трогает никакой текст.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Аддоны, в которых плагин ищет наведённое умение (ActionDetail рисует и подсказку на хотбаре, и панель в "Actions &amp; Traits"). Проверить имена: /xldata -> Addon Inspector.</summary>
    public List<string> TargetAddonNames { get; set; } = new()
    {
        "ActionDetail",
    };

    /// <summary>UTC-время последнего успешного обновления перевода с GitHub. Null = перевод ещё ни разу не скачивался.</summary>
    public DateTime? LastUpdateUtc { get; set; }

    /// <summary>Если true, плагин не прячет родную подсказку игры - она показывается вместе с нашим переводом (для сравнения/отладки).</summary>
    public bool ShowNativeTooltip { get; set; } = false;

    /// <summary>Если true, в правом верхнем углу нашей подсказки показывается ActionId наведённого умения (для отладки).</summary>
    public bool ShowActionId { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
