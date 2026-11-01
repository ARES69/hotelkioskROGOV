# Протокол моста: kiosk.html ↔ C#

Документ описывает **действующий** обмен между интерфейсом киоска (WebView2)
и бэкендом. Обработчик — `KioskActionAdapter` (`HotelKiosk.Application`),
приём и доставка — `MainWindow` (`HotelKiosk.UI`).

> Раньше в этом файле был описан протокол первой версии (`KioskBridgeHandler`,
> `findReservation` / `processPayment` / `writeKeys`). Эти классы удалены:
> они регистрировались в DI, но никогда не резолвились. Сейчас единственный
> путь — действия `MONE` ниже.

## Кто с кем говорит

```
kiosk.html  ──postMessage──▶  MainWindow  ──▶  KioskActionAdapter  ──▶  порты
     ▲                             │                                    │
     └──KioskBridge.receive(json)──┘◀───────────── результат ───────────┘
```

Мост принимает сообщения только из собственной страницы: источник сверяется
с загруженным `kiosk.html`, чужие сообщения отклоняются и пишутся в журнал.

## Конверт запроса

```json
{
  "type": "request",
  "version": 1,
  "action": "payment.create",
  "requestId": "req-17",
  "operationId": "op-3",
  "sessionId": "sess-42",
  "epoch": 1712345678901,
  "bridgeNonce": "n-1",
  "payload": { "method": "qr" }
}
```

| Поле | Назначение |
|---|---|
| `type` | Всегда `request`; прочие конверты игнорируются |
| `version` | Версия конверта, сейчас `1` |
| `action` | Имя действия из таблицы ниже |
| `requestId` | Корреляция ответа на стороне JS |
| `operationId` | Идентификатор бизнес-операции (сквозная корреляция) |
| `sessionId` | Сессия гостя. Смена `sessionId` или `epoch` сбрасывает состояние |
| `epoch` | Метка поколения сессии; защищает от ответов «прошлой» сессии |
| `bridgeNonce` | Эхо-поле, возвращается в ответе без изменений |
| `payload` | Параметры действия, объект |

## Конверт ответа

Успех:

```json
{
  "type": "response", "version": 1, "action": "booking.search",
  "requestId": "req-17", "operationId": "op-3", "sessionId": "sess-42",
  "epoch": 1712345678901, "bridgeNonce": "n-1",
  "ok": true, "result": { "found": true, "booking": { } }
}
```

Ошибка:

```json
{
  "type": "response", "version": 1, "action": "payment.status",
  "requestId": "req-18", "operationId": "op-3", "sessionId": "sess-42",
  "epoch": 1712345678901, "bridgeNonce": null,
  "ok": false, "error": { "code": "PAYMENT_AMOUNT_MISMATCH" }
}
```

`requestId`, `operationId`, `sessionId`, `epoch`, `bridgeNonce` всегда
возвращаются как пришли — по ним JS отличает свой ответ от чужого.

## Сессии

Состояние живёт в адаптере и привязано к паре `sessionId|epoch`. Новая пара —
полный сброс: бронь, гости, документы, платёж, чекпоинт. Это защита от того,
что гость ушёл, а следующий получил чужую бронь или чужой платёж.

## Действия

| Действие | Payload | Что делает |
|---|---|---|
| `system.preflight` | — | Готовность: PMS, Regula, VisionLine, камера/сканер. Возвращает `ready`, `devices{logus,vision,crt,regula,camera,scanner,pms}`, `captureSource`, `pms{vendor,missing}` |
| `booking.search` | `code`, `surname` | Поиск брони в PMS. Возвращает `found`, `booking{id,code,room,categoryRu,arrival,departure,total,dueAmount,status,roomReady,maxGuests}` |
| `booking.arrivals` | `date` | Заезды на дату для поиска по фамилии |
| `scanner.scan` | `sequence`, `pageType`, `purpose`, `image?` | Сканирование/распознавание паспорта. `image` — кадр с камеры (base64), иначе считыватель. Возвращает нормализованные поля гостя |
| `pms.guest.upsert` | `index`, при отсутствии скана — `lastNameRu`, `firstNameRu`, `middleNameRu` | Создаёт профиль гостя в PMS и привязывает к брони. Возвращает `guestId`, `guestDocsSent` |
| `pms.checkin` | `room` | Заселение. Требует подтверждённой оплаты. Возвращает `checkinId` |
| `vision.prepare` | `room` | Готовит доступ/энкодер. Возвращает `accessId` |
| `card.detect` | `index` | Обнаружение карты на энкодере |
| `card.read` | `uid` | Чтение карты |
| `card.write` | `uid`, `index`, `room` | Запись ключа. Возвращает `uid`, `written` |
| `card.verify` | `uid` | Проверка записанной карты |
| `card.awaitRemoval` | `uid` | Ожидание снятия карты |
| `pms.finalize` | `cards` | Завершение заселения. Возвращает `finalizeId` |
| `recovery.inspect` | — | Разбор чекпоинта прошлой сессии: незавершённое заселение, нужен ли возврат |
| `payment.create` | `method`: `qr` \| `card` | Создаёт заказ на оплату. Возвращает `orderId`, `confirmed`, `alreadyPaid`, `rrn`, `qrImageBase64`, `qrPayload`, `expiresAt` |
| `payment.status` | `orderId` | Опрос владельца заказа (банк/терминал). Возвращает `confirmed`, `rrn` |
| `payment.cancel` | — | Отмена: неоплаченный заказ отзывается, оплаченный — возвращается |

## Ошибки

Код приходит в `error.code`; текст для гостя формирует интерфейс.

| Код | Смысл |
|---|---|
| `API_VALIDATION` | Неизвестное действие или некорректный запрос |
| `API_SERVER_ERROR` | Непредвиденный сбой на стороне киоска |
| `BOOKING_NOT_FOUND` | Бронь не найдена |
| `BOOKING_DATES_INVALID` | Некорректные даты брони |
| `PMS_OFFLINE` | PMS недоступна |
| `PMS_CHECKIN_FAILED` | PMS отклонила заселение |
| `PMS_CHECKIN_UNKNOWN` | Заселение могло пройти, но подтверждение не получено — нужен разбор |
| `OCR_FIELD_MISSING` | Документ не распознан или не хватает полей |
| `OCR_DATA_CONFLICT` | Повторное сканирование дало другой документ |
| `OCR_DUPLICATE_DOCUMENT` | Этот документ уже привязан к другой странице |
| `CARD_WRITE_FAILED` | Ключ не записан |
| `PAYMENT_DECLINED` | Банк или терминал отклонили оплату |
| `PAYMENT_NOT_CONFIRMED` | Оплата не подтверждена — заселение запрещено |
| `PAYMENT_STATUS_UNKNOWN` | Статус оплаты недоступен — автоматические действия запрещены |
| `PAYMENT_AMOUNT_MISMATCH` | Списанная сумма не совпала с ценой брони |

## Правила денег

Эти правила — не детали реализации, а гарантии, на которые опирается UI.

1. **Шлагбаум заселения.** `pms.checkin` и `card.write` не выполняются без
   подтверждённой оплаты. Подтверждение спрашивается у владельца заказа
   (банк/терминал), а не у экрана.
2. **Сверка суммы.** Если владелец заказа сообщил сумму, она сверяется с ценой
   брони. Расхождение останавливает заселение, персонал получает уведомление.
   Сумма принимается и в рублях, и в копейках — шлюзы отдают по-разному.
3. **Идемпотентность.** Повторный `payment.create` в той же сессии не создаёт
   второй заказ: возвращается существующий (`alreadyPaid=true`, если уже
   оплачен). Двойной клик не приводит к двойному списанию.
4. **Отмена.** Неоплаченный заказ отзывается, оплаченный — возвращается на ту
   сумму, которую подтвердил владелец заказа. Если статус неизвестен, киоск не
   делает ни того, ни другого: разбирает сотрудник.
5. **Чекпоинт.** Перед заселением на диск пишется отметка с номером транзакции.
   Падение между оплатой и заселением не теряет деньги: при следующем старте
   `recovery.inspect` покажет, что нужен возврат.
