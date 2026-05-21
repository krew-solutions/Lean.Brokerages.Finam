# QuantConnect.Brokerages.Finam

LEAN brokerage-plugin for the [Finam Trade API](https://tradeapi.finam.ru).
Дает возможность торговать на Московской и СПб биржах (а также на американских рынках, к которым у Finam есть доступ) из алгоритмов QuantConnect LEAN.

> Status: **revision 0**. Покрывает базовый набор: размещение/отмена ордеров, получение позиций и баланса, исторические свечи, простую подписку на котировки. Стриминг через gRPC, опции, и SL/TP пока не реализованы.

---

## Из чего состоит репозиторий

| Папка | Назначение |
|---|---|
| `QuantConnect.FinamBrokerage/` | Главный плагин — реализация `IBrokerage`, `IDataQueueHandler`, фабрика, маппинги. |
| `QuantConnect.FinamBrokerage.Tests/` | Unit-тесты на NUnit. |
| `QuantConnect.FinamBrokerage.ToolBox/` | CLI-утилита `FinamDataDownloader` для выгрузки исторических данных. |
| `config-finam-example.json` | Пример секции конфигурации, которую нужно добавить в `Lean/Launcher/config.json`. |
| `Lean.Brokerages.Finam.sln` | Solution для сборки всех трёх проектов. |

## Архитектура

```
LEAN engine
   │
   │  IBrokerage / IDataQueueHandler   (orders, account, ticks)
   │
   ▼
FinamBrokerage  ──►  FinamApiClient  ──►  HTTP/JSON  ──►  api.finam.ru
   │                       │
   │                       └─► JWT lifecycle (auth + auto-refresh)
   │
   ├── FinamSymbolMapper          (LEAN Symbol <-> "TICKER@MIC")
   ├── FinamOrderMapping          (LEAN Order  <-> Finam Order)
   ├── FinamBrokerageModel        (capabilities, supported order types)
   └── FinamFeeModel              (комиссии MOEX / FORTS / US)
```

Плагин обращается к gRPC-Gateway REST-endpoint Finam — это удобнее для C# (не требует `Grpc.Net.Client`, работает поверх HttpClient), и контракт идентичен gRPC по полям.

## Сборка

```bash
cd Lean.Brokerages.Finam
dotnet restore
dotnet build -c Release
dotnet test
```

Требуется .NET 9 SDK и доступ к NuGet (`QuantConnect.Lean`).
DLL после сборки лежит в `QuantConnect.FinamBrokerage/bin/Release/net9.0/QuantConnect.Brokerages.Finam.dll`.
Скопируйте её в `Lean/Launcher/bin/Release/` рядом с остальными `QuantConnect.Brokerages.*.dll`.

## Конфигурация (`Lean/Launcher/config.json`)

```jsonc
{
  "environment": "live-finam",

  "finam-secret-token": "<API SECRET от https://tradeapi.finam.ru>",
  "finam-account-id":   "A12345",
  "finam-api-url":      "https://api.finam.ru",
  "finam-account-type": "margin",            // или "cash"

  "live-finam": {
    "live-mode": true,
    "live-mode-brokerage": "FinamBrokerage",

    "setup-handler":       "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler",
    "result-handler":      "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler",
    "data-feed-handler":   "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed",
    "data-queue-handler":  [ "QuantConnect.Brokerages.Finam.FinamBrokerage" ],
    "real-time-handler":   "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler",
    "transaction-handler": "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler",
    "history-provider":    "BrokerageHistoryProvider"
  }
}
```

## Что поддерживается

| Возможность | Статус | Пояснение |
|---|---|---|
| Авторизация (JWT) | ✅ | Auto-refresh через `/v1/sessions` |
| `PlaceOrder` (Market / Limit / Stop / StopLimit) | ✅ | REST `POST /v1/accounts/{id}/orders` |
| `CancelOrder` | ✅ | REST `DELETE /v1/accounts/{id}/orders/{oid}` |
| `UpdateOrder` | ❌ | Finam Trade API не имеет RPC `ModifyOrder` — алгоритм должен cancel+replace |
| `GetOpenOrders` | ✅ | REST `GET /v1/accounts/{id}/orders` |
| `GetAccountHoldings` | ✅ | REST `GET /v1/accounts/{id}` |
| `GetCashBalance` | ✅ | Из `cash` в ответе `GetAccount` |
| `GetHistory` (Minute/Hour/Daily) | ✅ | REST `GET /v1/instruments/{symbol}/bars` |
| `IDataQueueHandler` (live ticks) | ⚠️ | REST-поллинг `LastQuote` (2 сек). gRPC `SubscribeQuote` — todo. |
| SL/TP-заявки | ⚠️ | DTO готов, но `PlaceSLTPOrder` пока не вызывается из LEAN `Order`. |
| Опционы / Фьючерсы | ⚠️ | Базово работают через `FinamSymbolMapper`, но без OptionChain provider'а. |

## Маппинг символов

Finam использует формат `TICKER@MIC`:

| LEAN | Finam |
|---|---|
| `Symbol.Create("SBER", Equity, "finam")` | `SBER@MISX` |
| `Symbol.Create("AAPL", Equity, "USA")` | `AAPL@XNAS` |
| `Symbol.Create("Si-3.26", Future, "finam")` | `Si-3.26@RTSX` |

Если вы пишете уже квалифицированный тикер `"GAZP@MISX"` напрямую — `FinamSymbolMapper` сохранит его без изменений.

## Пример алгоритма

```csharp
public class FinamDemo : QCAlgorithm
{
    public override void Initialize()
    {
        SetTimeZone(TimeZones.Moscow);
        SetStartDate(2025, 1, 1);
        SetBrokerageModel(new FinamBrokerageModel());

        AddEquity("SBER", Resolution.Minute, market: "finam");
        AddEquity("GAZP", Resolution.Minute, market: "finam");
    }

    public override void OnData(Slice data)
    {
        if (!Portfolio.Invested && data.ContainsKey("SBER"))
        {
            SetHoldings("SBER", 0.5);
        }
    }
}
```

## Скачивание исторических данных

```bash
dotnet run --project QuantConnect.FinamBrokerage.ToolBox -- \
  --tickers=SBER,GAZP,LKOH \
  --resolution=minute \
  --from=2025-01-01 \
  --to=2025-02-01
```

Утилита печатает CSV `time,symbol,price` в stdout — перенаправьте в файл или адаптируйте на запись в каталог `Data/`.

## Дорожная карта

1. Заменить REST-поллинг `LastQuote` на gRPC-стрим `SubscribeQuote` / `SubscribeLatestTrades`.
2. Подписаться на `SubscribeOrders` / `SubscribeTrades` для пуш-обновлений статуса заявок (вместо `OrderEvent` сразу после REST-ответа).
3. Поддержать `PlaceSLTPOrder` через `BracketOrder` или кастомное расширение.
4. Подключить `OptionChainProvider` через `/v1/assets/{underlying}/options`.
5. Реализовать `FinamSymbolMapperFile` с офлайн-снимком universe инструментов.
6. Адаптировать LEAN `Market` enum: добавить `Market.Finam` в Lean common после мерджа upstream.

## Ссылки

- Finam Trade API (proto, swagger): https://github.com/FinamWeb/finam-trade-api
- LEAN brokerage contribution guide: `Lean-Documentation/06 LEAN Engine/02 Contributions/02 Brokerages/`
- QuantConnect docs — Brokerage Models: https://www.quantconnect.com/docs/v2/writing-algorithms/reality-modeling/brokerages
