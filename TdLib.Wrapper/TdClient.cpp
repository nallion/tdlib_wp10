#include "pch.h"
#include "TdClient.h"
#include <string>
#include <codecvt>

using namespace TdLib;
using namespace Platform;
using namespace Windows::Foundation;
using namespace concurrency;

// ── Хелперы конвертации строк ──────────────────────────────────────────────
static std::string PlatformToStd(Platform::String^ s)
{
    std::wstring ws(s->Data());
    std::wstring_convert<std::codecvt_utf8<wchar_t>> conv;
    return conv.to_bytes(ws);
}

static Platform::String^ StdToPlatform(const char* s)
{
    if (!s) return nullptr;
    std::wstring_convert<std::codecvt_utf8<wchar_t>> conv;
    std::wstring ws = conv.from_bytes(s);
    return ref new Platform::String(ws.c_str());
}

// ── Конструктор ────────────────────────────────────────────────────────────
TdClient::TdClient() : _client(nullptr), _running(false), _hTdJson(nullptr)
{
    if (!LoadTdJson())
        throw ref new Platform::Exception(-1, L"Не удалось загрузить tdjson.dll");

    _client = _td_create();

    // Отключаем лишние логи TDLib
    Execute(L"{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":1}");
}

TdClient::~TdClient()
{
    StopReceiving();
    if (_client && _td_destroy)
        _td_destroy(_client);
    if (_hTdJson)
        FreeLibrary(_hTdJson);
}

// ── Загрузка tdjson.dll через LoadLibrary ──────────────────────────────────
bool TdClient::LoadTdJson()
{
    _hTdJson = LoadPackagedLibrary(L"tdjson.dll", 0);
    if (!_hTdJson) return false;

    _td_create  = (td_json_client_create_t) GetProcAddress(_hTdJson, "td_json_client_create");
    _td_send    = (td_json_client_send_t)   GetProcAddress(_hTdJson, "td_json_client_send");
    _td_receive = (td_json_client_receive_t)GetProcAddress(_hTdJson, "td_json_client_receive");
    _td_execute = (td_json_client_execute_t)GetProcAddress(_hTdJson, "td_json_client_execute");
    _td_destroy = (td_json_client_destroy_t)GetProcAddress(_hTdJson, "td_json_client_destroy");

    return _td_create && _td_send && _td_receive && _td_execute && _td_destroy;
}

// ── Отправка запроса ───────────────────────────────────────────────────────
void TdClient::Send(Platform::String^ request)
{
    if (!_client) return;
    auto str = PlatformToStd(request);
    _td_send(_client, str.c_str());
}

// ── Синхронный execute ─────────────────────────────────────────────────────
Platform::String^ TdClient::Execute(Platform::String^ request)
{
    if (!_client) return nullptr;
    auto str = PlatformToStd(request);
    const char* result = _td_execute(_client, str.c_str());
    return StdToPlatform(result);
}

// ── Запуск фонового цикла получения апдейтов ──────────────────────────────
void TdClient::StartReceiving()
{
    if (_running) return;
    _running = true;

    _receiveTask = create_async([this]()
    {
        ReceiveLoop();
    });
}

void TdClient::StopReceiving()
{
    _running = false;
}

void TdClient::ReceiveLoop()
{
    while (_running)
    {
        const char* result = _td_receive(_client, 1.0); // таймаут 1 сек
        if (result)
        {
            auto str = StdToPlatform(result);
            // Маршалим событие в UI поток
            UpdateReceived(str);
        }
    }
}
