# FFXIV JobGuide RU Translator

Dalamud-плагин для FINAL FANTASY XIV. Подменяет **только текст описания умения** (не название)
во всплывающей подсказке умения и в панели описания окна **Actions & Traits** на русский перевод
из проекта [FFXIVJobGuideRU](https://github.com/Murakumo-JP/FFXIVJobGuideRU) /
[ff14jobguide.ru](https://ff14jobguide.ru/JobGuide). Больше нигде в интерфейсе плагин ничего не меняет.

> Названия умений остаются как в игре (на языке вашего клиента) — в исходном переводе они не
> локализованы, локализовано только описание.

## Как это устроено (коротко)

- Переводы (`DB/*.json` из репозитория FFXIVJobGuideRU) один раз скачаны и **вшиты в саму сборку**
  плагина (`Data/SourceJson/`) — плагин работает офлайн. Обновить их можно вручную командой
  `/jgru update` (перекачивает JSON с GitHub, ничего не делает автоматически/в фоне).
- Английское название умения из перевода сопоставляется с реальным `ActionId` игры через листы
  Excel-данных `Action` (бой) и `CraftAction` (крафт/сбор) — так плагин узнаёт, какому умению
  какой перевод принадлежит.
- Подмена текста делается через официальный API Dalamud `IAddonLifecycle` (событие `PostDraw`)
  на конкретных нативных окнах (аддонах) — по умолчанию `Tooltip` и `ActionDetail`. Плагин ищет
  текстовую ноду с описанием не по жёстко зашитому ID, а по совпадению текста, поэтому переживёт
  мелкие патчи игры, но имена аддонов и разметку стоит один раз проверить руками (см. ниже).

## Важно понимать заранее

Я написал весь код без доступа к запущенному клиенту FFXIV — собрать и проверить его можете
только вы, на своей машине с игрой и Dalamud. Вероятно, с первого раза придётся поправить одну-две
мелочи (см. раздел "Если не заработало"). Это не баг в смысле "всё сломано" — просто часть
внутренних имён окон игры невозможно узнать без живого клиента.

## Требования

- [XIVLauncher](https://xivlauncher.app/) с установленным Dalamud (запустите игру через него хотя бы раз).
- [.NET SDK 9.0](https://dotnet.microsoft.com/download) (или та версия, которую требует текущий `Dalamud.NET.Sdk` — см. первую строку `FFXIVJobGuideRUTranslator.csproj`, сейчас `15.0.0`).
- Visual Studio 2022 или JetBrains Rider (необязательно, но удобнее, чем голый `dotnet build`).
- В XIVLauncher: `Настройки Dalamud -> Экспериментальное -> Developer Mode` включён (нужно, чтобы подключить локальный dev-плагин).

## Сборка

```bash
cd FFXIVJobGuideRUTranslator
dotnet build -c Debug
```

`Dalamud.NET.Sdk` сам подтянет нужные ссылки на сборки Dalamud/FFXIVClientStructs/Lumina с вашей
локальной установки Dalamud (через переменную окружения `DALAMUD_HOME`, которую XIVLauncher
обычно выставляет сам; если сборка не находит Dalamud — задайте `DALAMUD_HOME` на папку
`%AppData%\XIVLauncher\addon\Hooks\dev`).

## Установка как dev-плагин

1. В игре: `/xlsettings` -> вкладка `Experimental` -> `Dev Plugin Locations` -> добавить папку
   `FFXIVJobGuideRUTranslator\bin\x64\Debug` (там после сборки лежит `FFXIVJobGuideRUTranslator.dll`
   и манифест).
2. `/xlplugins` -> вкладка `Dev Tools` -> найти плагин в списке -> включить.
3. После правок кода: пересобрать (`dotnet build`) и в `/xlplugins -> Dev Tools` нажать перезагрузку
   плагина (не обязательно перезапускать игру целиком).

## Проверка и настройка (сделать один раз)

Плагин слушает окна (аддоны) по имени — список задаётся в настройках плагина (`/jgru`), по
умолчанию `Tooltip` и `ActionDetail`. Эти имена — моё предположение по внешнему виду ваших
скриншотов; конкретно в вашей версии игры они могут отличаться. Проверить:

1. В игре выполните `/xldata`, откроется окно данных Dalamud.
2. Откройте вкладку **Addon Inspector**.
3. Наведите курсор на умение на хотбаре (как на первом скриншоте) — в Addon Inspector должно
   подсветиться/появиться активное окно с описанием умения. Посмотрите его имя (Name).
4. Сделайте то же самое, наведя курсор на умение в списке окна **Actions & Traits**
   (как на втором скриншоте).
5. Если имена отличаются от `Tooltip`/`ActionDetail` — откройте `/jgru`, уберите неверные и
   добавьте настоящие в поле "Добавить аддон". Изменения применяются сразу, без пересборки.

## Обновление перевода

- `/jgru update` — скачивает свежие `DB/*.json` с GitHub и сохраняет их в папке конфигурации
  плагина (`%AppData%\XIVLauncher\pluginConfigs\FFXIVJobGuideRUTranslator\translations`).
  С этого момента используются они, а не вшитый в сборку бандл.
- `/jgru reload` — удаляет скачанные файлы и возвращает встроенный в плагин перевод.
- Открыть окно настроек: `/jgru` без аргументов. Там же — счётчики "загружено / сопоставлено",
  по ним видно, насколько успешно прошло сопоставление названий с ID умений игры.

## Если не заработало

Скорее всего дело в одном из мест, которые нельзя было проверить без клиента:

- **Ничего не подменяется нигде.** Проверьте счётчик "Сопоставлено с ID умений игры" в `/jgru` -
  если 0, значит листы `Lumina.Excel.Sheets.Action` / `Lumina.Excel.Sheets.CraftAction` не
  совпали по имени (например, из-за разницы в написании между сайтом и игрой, или в вашей версии
  Lumina эти типы называются иначе — проверьте в IntelliSense namespace `Lumina.Excel.Sheets`).
- **Подменяется не в том окне / не то поле.** Значит имя аддона неверное или структура окна
  сложнее, чем предполагает эвристика "самая длинная текстовая нода" в
  `Hooks/AbilityTextTranslator.cs` (метод `ReplaceDescriptionNode`) - поправьте её под то, что
  реально видите через Addon Inspector.
- **Ошибка компиляции в `AbilityTextTranslator.cs` на строке с `args.Addon` или `node->SetText`.**
  Это низкоуровневый API FFXIVClientStructs, который иногда меняет форму между версиями Dalamud -
  в коде оставлены комментарии с подсказкой, что попробовать вместо этого.

## Структура проекта

```
FFXIVJobGuideRUTranslator/
  FFXIVJobGuideRUTranslator.csproj   - манифест плагина и настройки сборки
  Plugin.cs                          - точка входа, регистрация команды /jgru
  Configuration.cs                   - настройки плагина (вкл/выкл, список аддонов)
  Data/
    SourceJson/**                    - вшитый в сборку бандл переводов (DB/*.json из FFXIVJobGuideRU)
    RawSourceParsing.cs               - разбор JSON-схемы сайта (в т.ч. вариантов для MIN/BTN и т.п.)
    TranslationEntry.cs               - одна распознанная запись перевода
    TranslationRepository.cs          - сопоставление перевода с ActionId игры, поиск по имени/ID
    TranslationUpdater.cs             - ручное обновление перевода с GitHub (команда /jgru update)
  Hooks/
    AbilityTextTranslator.cs          - подмена текста через IAddonLifecycle
  Windows/
    ConfigWindow.cs                   - окно настроек (/jgru)
```

## Источники

- Перевод: [Murakumo-JP/FFXIVJobGuideRU](https://github.com/Murakumo-JP/FFXIVJobGuideRU), [ff14jobguide.ru](https://ff14jobguide.ru/JobGuide)
- Платформа: [goatcorp/Dalamud](https://github.com/goatcorp/Dalamud), шаблон проекта [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin)
