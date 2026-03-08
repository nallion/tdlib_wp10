# TelegramWP10 — Telegram клиент для Windows Phone 10

Минималистичный Telegram клиент на базе TDLib для UWP / Windows Phone 10.

## Структура проекта

```
TelegramWP10/
├── TdLib.Wrapper/          ← C++/CX WinRT Component (оборачивает tdjson.dll)
│   ├── TdClient.h
│   ├── TdClient.cpp
│   └── [tdjson.dll, LIBEAY32.dll, SSLEAY32.dll, zlib1.dll]  ← скопируй сюда
│
└── TelegramClient/         ← C# UWP приложение
    ├── TdService.cs        ← вся логика работы с TDLib
    ├── LoginPage.xaml/.cs  ← авторизация (телефон → код → пароль)
    ├── ChatsPage.xaml/.cs  ← список чатов
    └── ChatPage.xaml/.cs   ← окно чата
```

## Настройка перед сборкой

### 1. Получи API ключи
Зайди на https://my.telegram.org → API development tools
Получи `api_id` (число) и `api_hash` (строка)

### 2. Вставь ключи в LoginPage.xaml.cs
```csharp
_td.Initialize(YOUR_API_ID, "YOUR_API_HASH");
// Замени YOUR_API_ID на число, "YOUR_API_HASH" на строку
```

### 3. Скопируй DLL артефакты в проект TdLib.Wrapper
```
tdjson.dll
LIBEAY32.dll
SSLEAY32.dll
zlib1.dll
```
В свойствах каждого файла в Visual Studio:
- Build Action: **Content**
- Copy to Output Directory: **Copy always**

### 4. Настрой референс между проектами
В TelegramClient → References → Add Reference → Projects → TdLib.Wrapper

### 5. Установи NuGet пакет
В TelegramClient:
```
Install-Package Newtonsoft.Json
```

## Создание Solution в Visual Studio 2017

1. File → New → Project → **Blank App (Universal Windows)** → C#
   - Назови: `TelegramClient`
   - Target: Windows 10 (10.0.16299), Min: 10.0.10586

2. Add → New Project → **Windows Runtime Component (Universal Windows)** → C++
   - Назови: `TdLib.Wrapper`

3. Замени содержимое файлов кодом из этого репозитория

4. В TdLib.Wrapper добавь в `.vcxproj` в секцию `<PropertyGroup>`:
```xml
<PlatformToolset>v141</PlatformToolset>
```

## Целевые платформы
- Windows Phone 10 (ARM)
- Windows 10 Desktop (x64) — тоже работает для отладки

## Зависимости
- TDLib tdjson.dll (собранный под arm-uwp)
- Newtonsoft.Json (NuGet)
- Visual Studio 2017 с компонентами UWP + C++
