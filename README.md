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
   │  IBrokerage / IDataQueueHandler   (orders, account, ticks/bars)
   │
   ▼
FinamBrokerage
   │
   ├── FinamApiClient ──────► HTTPS/JSON ──► api.finam.ru        (orders, account, history, fallback-quotes)
   │        └─► JWT lifecycle (auth + auto-refresh)
   │
   ├── FinamWebSocketClient ► wss://api.finam.ru/ws             (live QUOTES / INSTRUMENT_TRADES ticks; ORDERS / TRADES)
   │        └─► Authorization: <jwt> header + auto-reconnect/replay
   │
   ├── FinamSymbolMapper          (LEAN Symbol <-> "TICKER@MIC")
   ├── FinamOrderMapping          (LEAN Order  <-> Finam Order)
   ├── FinamBrokerageModel        (capabilities, supported order types)
   └── FinamFeeModel              (комиссии MOEX / FORTS / US)
```

- **Управление заявками / счёт / история** идут через gRPC-Gateway REST Finam (поверх `HttpClient`, без `Grpc.Net.Client`; контракт совпадает с gRPC по полям).
- **Live-данные** — стандартный путь LEAN через `LevelOneServiceManager` (как в брокерах Coinbase / Tastytrade / ThetaData): из WebSocket приходят **тики** — `QUOTES` → `HandleQuote`, `INSTRUMENT_TRADES` → `HandleLastTrade` — а движок (`IDataAggregator`) сам консолидирует их в бары нужной резолюции. WS-`BARS` для live не используется; история — отдельно, REST в `GetHistory`.
- **Дедуп ленты:** на (пере)подписку Finam досылает снапшот недавних сделок, поэтому `INSTRUMENT_TRADES` фильтруется по 5-мин фронтиру + последнему `trade_id` на символ. Account-fills (`TRADES`) дедупятся по `trade_id`, чтобы reconnect не задвоил исполнение.
- **Авторизация WS** — JWT в заголовке `Authorization` при коннекте; тот же токен дублируется в обязательном поле `token` каждого сообщения-подписки.
- **Авторизация WS** — JWT в заголовке `Authorization` при коннекте; тот же токен дублируется в обязательном поле `token` каждого сообщения-подписки.

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
| `IDataQueueHandler` — live тики | ✅ | `LevelOneServiceManager`: WS `QUOTES` → `HandleQuote`, `INSTRUMENT_TRADES` → `HandleLastTrade`; дедуп ленты (5-мин фронтир + `trade_id`) |
| Live-бары (Minute/Hour/Daily/…) | ✅ | Строит движок из тиков (`IDataAggregator`); WS-`BARS` для live не нужен |
| WS auto-reconnect + replay подписок | ✅ | Экспоненциальный backoff, повторная авторизация свежим JWT |
| Push fills с реальной ценой (WS `TRADES` по счёту) | ✅ | `OrderEvent.FillPrice/FillQuantity` из `AccountTrade` (`order_id`+`price`), дедуп по `trade_id`; `PlaceOrder` не шлёт «оптимистичный» `Filled` |
| Push статусов заявок (WS `ORDERS` по счёту) | ✅ | Submitted/Canceled/Rejected push'ем; дедуп по (order, status) |
| Комиссия в fill-событии | ⚠️ | `AccountTrade` не несёт комиссию (Finam шлёт её отдельной COMMISSION-транзакцией) — fill идёт с `OrderFee.Zero`, реальные сборы сверяются через cash sync |
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

1. ~~Стрим рыночных данных.~~ ✅ Live-тики через WS `QUOTES`/`INSTRUMENT_TRADES` → `LevelOneServiceManager`, движок консолидирует в бары (идиом Coinbase/Tastytrade/ThetaData).
2. ~~WS `ORDERS`/`TRADES` для push статуса заявок и исполнений.~~ ✅ fills с реальной ценой из `AccountTrade`, статусы из `ORDERS`. Остался WS `ACCOUNT` (push портфеля), проброс комиссии из COMMISSION-транзакций, и REST-ресинк заявок/fills при долгом разрыве WS.
3. Поддержать `PlaceSLTPOrder` через `BracketOrder` или кастомное расширение.
4. Подключить `OptionChainProvider` через `/v1/assets/{underlying}/options`.
5. Реализовать `FinamSymbolMapperFile` с офлайн-снимком universe инструментов.
6. Адаптировать LEAN `Market` enum: добавить `Market.Finam` в Lean common после мерджа upstream.

## Ссылки

- Finam Trade API (proto, swagger): https://github.com/FinamWeb/finam-trade-api
- LEAN brokerage contribution guide: `Lean-Documentation/06 LEAN Engine/02 Contributions/02 Brokerages/`
- QuantConnect docs — Brokerage Models: https://www.quantconnect.com/docs/v2/writing-algorithms/reality-modeling/brokerages
