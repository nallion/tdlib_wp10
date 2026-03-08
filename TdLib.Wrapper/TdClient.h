#pragma once
#include <collection.h>
#include <ppltasks.h>

// Подключаем tdjson API
extern "C" {
    typedef void* (*td_json_client_create_t)();
    typedef void  (*td_json_client_send_t)(void*, const char*);
    typedef const char* (*td_json_client_receive_t)(void*, double);
    typedef const char* (*td_json_client_execute_t)(void*, const char*);
    typedef void  (*td_json_client_destroy_t)(void*);
}

namespace TdLib
{
    // Делегаты для событий
    public delegate void UpdateReceivedHandler(Platform::String^ json);

    // Основной клиент — WinRT Component обёртка над tdjson.dll
    public ref class TdClient sealed
    {
    public:
        TdClient();
        virtual ~TdClient();

        // Отправить запрос (JSON строка)
        void Send(Platform::String^ request);

        // Синхронный execute (для setLogVerbosityLevel и т.п.)
        Platform::String^ Execute(Platform::String^ request);

        // Запустить фоновый поток получения апдейтов
        void StartReceiving();
        void StopReceiving();

        // Событие — новый апдейт/ответ от TDLib
        event UpdateReceivedHandler^ UpdateReceived;

    private:
        void* _client;
        bool  _running;
        Windows::Foundation::IAsyncAction^ _receiveTask;

        HMODULE _hTdJson;
        td_json_client_create_t  _td_create;
        td_json_client_send_t    _td_send;
        td_json_client_receive_t _td_receive;
        td_json_client_execute_t _td_execute;
        td_json_client_destroy_t _td_destroy;

        void ReceiveLoop();
        bool LoadTdJson();
    };
}
