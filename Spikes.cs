using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockSharp.Algo;
using StockSharp.BusinessEntities;
using StockSharp.Logging;
using StockSharp.Messages;

namespace PrismaBoy
{
    // Объявляем делегат обработчика события ПЕРЕД TimeFrame стратегии
    public delegate void TimeFrameEventBeforeHandler(object sender, TimeFrameEventBeforeArgs e);

    // Объявляем класс объекта (и его конструктор) для передачи параметров при событии ПЕРЕД TimeFrame стратегии
    public class TimeFrameEventBeforeArgs : EventArgs
    {
        public DateTime MarketTime { get; private set; }

        public TimeFrameEventBeforeArgs(DateTime marketTime)
        {
            MarketTime = marketTime;
        }
    }

    sealed class Spikes: MyBaseStrategy
    {
        private readonly decimal _deltaPercent;                                             // Дельта стратегии, %
        private readonly decimal _deltaFarPercent;                                          // Дальняя дельта стратегии, %
        private decimal _delta;                                                             // Дельта стратегии
        private decimal _deltaFar;                                                          // Дальняя дельта стратегии

        private readonly Dictionary<string, decimal> _securityVolumeDictionaryPlus;         // Дополнительные объемы
        private readonly string _singleFarCode;                                             // Код инструмента, по которому не будет выставляться стандартная заявка
        private TimeSpan _mySpanBefore;                                                     // Заглушка по времени (чтобы не регистрировалось много ордеров)
        public bool IsEveningOnly;                                                          // Заглушка по времени (чтобы не регистрировалось много ордеров)

        #region События

        public event TimeFrameEventBeforeHandler TimeFrameComeBefore;       // Делегат обработчика изменения свойств стратегии
        /// <summary>
        /// Метод обработки события прихода времени по TimeFrame
        /// </summary>
        public void OnTimeFrameComeBefore(TimeFrameEventBeforeArgs e)
        {
            var handler = TimeFrameComeBefore;
            if (handler != null) handler(this, e);
        }

        #endregion

        /// <summary>
        /// Конструктор
        /// </summary>
        public Spikes(List<Security> securityList, Dictionary<string, decimal> securityVolumeDictionary, TimeSpan timeFrame, decimal stopLossPercent, decimal takeProfitPercent, Dictionary<string, decimal> securityVolumeDictionaryPlus, decimal deltaPercent, decimal deltaFarPercent, bool? isTestContour)
            : base(securityList, securityVolumeDictionary, timeFrame, stopLossPercent, takeProfitPercent)
        {
            Name = "Spikes";
            IsIntraDay = true;
            TimeToStartRobot.Hours = 18;
            TimeToStartRobot.Minutes = 41;
            StopType = StopTypes.MarketLimitOffer;

            // В соответствии с параметрами конструктора
            _deltaPercent = deltaPercent;
            _deltaFarPercent = deltaFarPercent;
            _securityVolumeDictionaryPlus = securityVolumeDictionaryPlus;
            _singleFarCode = (bool)(!isTestContour)
                                        ? "LK"
                                        : "Si";

            // Задаем выход по времени через 2 бара
            BarsToClose = 2;
        }

        /// <summary>
        /// Событие старта стратегии
        /// </summary>
        protected override void OnStarted()
        {
            TimeToStopRobot = IsWorkContour
                                              ? new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 19,
                                                             26, 00)
                                              : new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 23,
                                                             44, 00);

            TimeFrameComeBefore -= SpikesTimeFrameComeBefore;
            TimeFrameComeBefore += SpikesTimeFrameComeBefore;

            this.AddInfoLog("Стратегия запускается со следующими параметрами:" +
                            "\nТаймфрейм: " + TimeFrame +
                            "\nДельта, %: " + _deltaPercent +
                            "\nДельта дальняя, %: " + _deltaFarPercent +
                            "\nСтоплосс, %: " + StopLossPercent);

            base.OnStarted();

            // Переподписываемся на событие изменения биржевого времени
            Connector.MarketTimeChanged -= SpikesMarketTimeChanged;
            Connector.MarketTimeChanged += SpikesMarketTimeChanged;
        }

        /// <summary>
        /// Обработчик события совершения сделки
        /// </summary>
        protected override void OnNewMyTrades(IEnumerable<MyTrade> trades)
        {
            //Для каждого совершённого трейда
            foreach (var trade in trades)
            {
                // Передаем данные об изменившейся позиции в словарь
                PositionsDictionary[trade.Trade.Security.Code] = trade.Order.Direction == Sides.Buy
                                                                     ? PositionsDictionary[trade.Trade.Security.Code] +=
                                                                       trade.Trade.Volume
                                                                     : PositionsDictionary[trade.Trade.Security.Code] -=
                                                                       trade.Trade.Volume;

                if (!trade.Order.Comment.StartsWith(Name + ", enter")) continue;

                this.AddInfoLog("ВХОД - {0} , TranActionID - {1}", trade.Trade.Security.Code, trade.Trade.Id);

                var actTradeDirection = trade.Order.Direction == Sides.Buy ? Direction.Buy : Direction.Sell;
                var actStopPrice = actTradeDirection == Direction.Buy
                                       ? trade.Trade.Security.ShrinkPrice(trade.Trade.Price * (1 - StopLossPercent / 100))
                                       : trade.Trade.Security.ShrinkPrice(trade.Trade.Price * (1 + StopLossPercent / 100));


                var newActiveTrade = new ActiveTrade(trade.Trade.Id, trade.Trade.Security.Code, actTradeDirection,
                                                     trade.Trade.Price, trade.Trade.Volume, trade.Trade.Time, actStopPrice, trade.Order.Comment);
                // Добавляем информацию о сделке в коллекцию активных сделок, в том числе для того, чтобы отрабатывать СтопЛосс
                ActiveTrades.Add(newActiveTrade);

                // Вызываем событие прихода изменения ActiveTrades
                OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                // Регистрируем стопОрдер сразу, если работаем не с Plaza
                if (MainWindow.Instance.ConnectorType != ConnectorTypes.Plaza)
                    trade.Trade.Security.WhenTimeCome(GetNextBarTime(trade.Trade.Time)).Once().Do(() => PlaceStopOrder(newActiveTrade, StopType)).Apply(this);

                // Если установлен положительный тейкпрофит, то регистрируем профитОрдер
                if (TakeProfitPercent > 0)
                    PlaceProfitOrder(newActiveTrade);

                // Определяем время выхода из сделки через 2 свечки, если не сработал стоплосс
                if (BarsToClose <= 0) continue;

                var timeToClose = SetCloseTime(trade.Trade.Time, 2);

                // И включаем выход из сделки через заданное количество свечей
                trade.Trade.Security.WhenTimeCome(timeToClose).Do(() => ClosePositionByTime(newActiveTrade)).Apply(this);

                this.AddInfoLog("ВЫХОД:\nЗАЯВКА на ВЫХОД по ВРЕМЕНИ - {0}. Регистрируемся на выход по инструменту в {1}, Id сделки - {2}", trade.Trade.Security.Code, timeToClose.ToString(CultureInfo.InvariantCulture), trade.Trade.Id);
            }
        }

        /// <summary>
        /// Обработчик события появления новых сделок (для реализации защиты по стопу). Специальный, для Spikes, так как первые 5 минут проводим без стопа.
        /// </summary>
        protected override void TraderNewTrades(IEnumerable<Trade> newTrades)
        {
            foreach (var newTrade in newTrades.Where(newTrade => SecurityVolumeDictionary.Any(item => newTrade.Security.Code == item.Key)))
            {
                // Обновляем Gross стратегии
                Gross += (newTrade.Price - LastTradesDictionary[newTrade.Security.Code]) *
                         PositionsDictionary[newTrade.Security.Code];

                // Обновляем информацию о цене последней сделки по активному инструменту
                LastTradesDictionary[newTrade.Security.Code] = newTrade.Price;

                // Если работаем не с Plaza, то более ничего не делаем
                if (MainWindow.Instance.ConnectorType != ConnectorTypes.Plaza) continue;

                foreach (var activeTrade in ActiveTrades)
                {
                    if (activeTrade.Security != newTrade.Security.Code || activeTrade.IsStopOrderPlaced) continue;

                    // Вычисляем есть ли ситуация стопа
                    var isStop = activeTrade.Direction == Direction.Buy
                                     ? newTrade.Price <= activeTrade.StopPrice
                                     : newTrade.Price >= activeTrade.StopPrice;

                    if (!isStop || newTrade.Time < GetNextBarTime(activeTrade.Time))
                        continue;
                    
                    // Устанавливаем флаг, что стоп заявка размещена
                    activeTrade.IsStopOrderPlaced = true;
                    
                    PlaceStopOrder(activeTrade, StopType);
                }
            }
        }

        /// <summary>
        /// Метод определения времени открытия следующего бара
        /// </summary>
        private DateTime GetNextBarTime(DateTime timeFrom)
        {
            int outReminder;

            var nextBarTime = timeFrom.Subtract(TimeSpan.FromSeconds(timeFrom.Second));

            Math.DivRem(nextBarTime.Minute, (int)TimeFrame.TotalMinutes, out outReminder);

            nextBarTime = nextBarTime.Subtract(TimeSpan.FromMinutes(outReminder));
            nextBarTime = nextBarTime.Add(TimeSpan.FromMinutes((int)TimeFrame.TotalMinutes));

            return nextBarTime;
        }

        /// <summary>
        /// Метод определяем времени закрытия сделки, если не сработал стоплосс
        /// </summary>
        protected override DateTime SetCloseTime(DateTime timeFrom, int bars)
        {
            return base.SetCloseTime(timeFrom, bars).Subtract(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Метод обработки события прихода времени для закрытия сделки, если не сработал стоплосс
        /// </summary>
        protected override void ClosePositionByTime(ActiveTrade trade)
        {
            // Если коллекция активных сделок не содержит трейд, то временной ордер не должен отрабатываться
            if (ActiveTrades.All(activeTrade => activeTrade != trade))
                return;

            var currentSecurity = SecurityList.First(sec => sec.Code == trade.Security);

            // Устанавливаем параметры временного ордера
            var closeByTimeOrder = new Order
            {
                Comment = Name + ",t," + trade.Id,
                Type = OrderTypes.Limit,
                Portfolio = Portfolio,
                Security = currentSecurity,
                Volume = trade.Volume,
                Direction =
                    trade.Direction == Direction.Sell
                        ? Sides.Buy
                        : Sides.Sell,
                Price =
                    trade.Direction == Direction.Sell
                        ? currentSecurity.ShrinkPrice(currentSecurity.BestBid.Price * (1 + 0.001m))
                        : currentSecurity.ShrinkPrice(currentSecurity.BestAsk.Price * (1 - 0.001m)),
            };

            // После срабатывания временного ордера, выводим сообщение в лог и останавливаем защитную стратегию

            closeByTimeOrder
                .WhenRegistered()
                .Once()
                .Do(() => this.AddInfoLog(
                        "ВЫХОД по ВРЕМЕНИ - {0}. Зарегистрирована заявка на выход из сделки впереди лучшей цены {1} в стакане.",
                        trade.Security,
                        closeByTimeOrder.Direction == Sides.Buy ? "Bid" : "Ask"))
                .Apply(this);

            closeByTimeOrder
                .WhenNewTrades()
                .Do(newTrades =>
                {
                    //foreach (var newTrade in newTrades)
                    //{
                    //    foreach (var activeTrade in ActiveTrades.Where(activeTrade => activeTrade.Id == trade.Id))
                    //    {
                    //        activeTrade.Volume -= newTrade.Trade.Volume;

                    //        // Вызываем событие прихода изменения ActiveTrades
                    //        OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                    //        if (activeTrade.Volume != 0)
                    //        {
                    //            this.AddInfoLog("Новый объем активной сделки с ID {0} - {1}",
                    //                  activeTrade.Id, activeTrade.Volume);
                    //        }
                    //        else
                    //        {
                    //            this.AddInfoLog("Новый объем активной сделки с ID {0} стал равен 0! Удаляем активную сделку и отменяем соответствующие заявки",
                    //                  activeTrade.Id);
                    //        }
                    //    }
                    //}
                })
                .Apply(this);


            closeByTimeOrder
                .WhenMatched()
                .Do(() =>
                {
                    ActiveTrades = ActiveTrades.Where(activeTrade => activeTrade != trade).ToList();

                    // Вызываем событие прихода изменения ActiveTrades
                    OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                    var ordersToCancel = Connector.Orders.Where(
                        order => order != null &&
                        ((order.Comment.EndsWith(trade.Id.ToString(CultureInfo.CurrentCulture)) &&
                          order.State == OrderStates.Active)));

                    //Если нет других активных ордеров связанных с данным активным трейдом, то ничего не делаем
                    if (!ordersToCancel.Any())
                        return;

                    // Иначе удаляем все связанные с данным активным трейдом ордера
                    foreach (var order in ordersToCancel)
                    {
                        Connector.CancelOrder(order);
                    }

                    this.AddInfoLog(
                        "ВЫХОД по ВРЕМЕНИ - {0}. Вышли из сделки впереди лучшей цены {1} в стакане.",
                        trade.Security,
                        closeByTimeOrder.Direction == Sides.Buy ? "Bid" : "Ask");
                })
                        .Apply(this);

            RegisterOrder(closeByTimeOrder);
        }

        /// <summary>
        /// Событие изменения биржевого времени
        /// </summary>
        private void SpikesMarketTimeChanged(TimeSpan time)
        {
            // Если стратегия в процессе остановки или стратегия не является таймфреймовой, то выходим
            if (TimeFrame == TimeSpan.Zero || ProcessState == ProcessStates.Stopping || ProcessState == ProcessStates.Stopped)
                return;

            // Если выходные, то выходим
            if (DateTime.Now.DayOfWeek == DayOfWeek.Saturday || DateTime.Now.DayOfWeek == DayOfWeek.Sunday)
                return;

            // Увеличиваем счетчик промежутка времени
            _mySpanBefore = _mySpanBefore.Add(time);

            // Если счетчик превышает 20 сек, то можно выставлять заявки
            var canRegisterBefore = _mySpanBefore.TotalMilliseconds > 5000;

            // Создаем и инициализируем временнЫе переменные
            var marketTime = Connector.GetMarketTime(Exchange.Moex);
            var currentTime = Connector.GetMarketTime(Exchange.Moex).Minute + Connector.GetMarketTime(Exchange.Moex).Hour * 60;

            #region Проверка рабочего времени

            if (IsWorkContour)  // TimeFilter для рабочего контура
            {
                if (currentTime < 600)                              // До начала дневной сессии
                    return;
                if (currentTime >= 840 && currentTime < 845)        // Пром клиринг
                    return;
                if (currentTime >= 1125 && currentTime < 1140)      // Вечерний клиринг
                    return;
                if (currentTime >= 1430 - TimeFrame.Minutes)        // Конец календарного дня (не выставляем заявки позже 23:30)
                    return;
            }
            else              // TimeFilter для тестового контура
            {
                if (currentTime < 600)                              // До начала дневной сессии
                    return;
                if (currentTime >= 700 && currentTime < 740)        // Пром клиринг          
                    return;
                if (currentTime >= 870 && currentTime < 930)        // Дневной клиринг
                    return;
                if (currentTime >= 1125 && currentTime < 1140)      // Вечерний клиринг
                    return;
                if (currentTime >= 1430 - TimeFrame.Minutes)        // Конец календарного дня (не выставляем заявки позже 23:30)
                    return;
            }

            #endregion

            // Если время 18:44 - то выставляем заявку чуть раньше, чтобы успеть до вечернего клиринга и не промахнуться из-за рассинхронизации по времени
            var lastSecond = (currentTime == 839 || currentTime == 1124) ? 57 : 59;

            // Если НЕ наступило время в соответствии с ПЕРЕД таймфремом или стоит запрет на выставление заявок, то выходим
            if ((marketTime.AddSeconds(5).Minute) % TimeFrame.Minutes != 0 || marketTime.Second != (lastSecond - 2) ||
                marketTime.Millisecond < 500 || !canRegisterBefore) return;

            // Сбрасываем счетчик
            _mySpanBefore = TimeSpan.Zero;

            // Вызываем событие прихода времени ТАЙМФРЕЙМа
            OnTimeFrameComeBefore(new TimeFrameEventBeforeArgs(marketTime));
        }

        /// <summary>
        /// Обработчик события прихода новой свечки
        /// </summary>
        protected override void TimeFrameCome(object sender, MainWindow.TimeFrameEventArgs e)
        {
            base.TimeFrameCome(sender, e);

            var currTime = e.MarketTime.Minute + e.MarketTime.Hour * 60;

            foreach (var security in SecurityList.Where(security => Orders != null))
            {
                if (Orders.Any())
                {
                    var tempSecurity = security;
                    foreach (var order in Orders.Where(order => order.Security == tempSecurity).Where(order => order.State == OrderStates.Active && order.Comment.EndsWith("enter")).Where(order => order != null))
                    {
                        Connector.CancelOrder(order);
                    }
                }

                if(IsEveningOnly)
                {
                    // НЕ пытаемся выставить заявки, если не попали во временной фильтр или, если находимся в позиции
                    if (IsWorkContour)
                        if (currTime < 1120 || currTime > 1150 || ActiveTrades.Count(trade => trade.Security == security.Code) != 0 || security.Code.StartsWith(_singleFarCode))
                            continue;
                    if (!IsWorkContour)
                        if (currTime < 990 || currTime > 1005 || GetCurrentPosition(security) != 0 || security.Code.StartsWith(_singleFarCode))
                            continue;
                }
                else
                {
                    // НЕ пытаемся выставить заявки  позже 23:30 или если есть активные трейды по соответствующему инструменту
                    if (currTime > 1420 || ActiveTrades.Count(trade => trade.Security == security.Code) != 0) continue;
                }
                

                // Вычисляем дельту от цены последней сделки
                var lastPrice = security.LastTrade.Price;
                _delta = Math.Round(lastPrice * _deltaPercent / 100, 0);

                var orderBuy = new Order
                {
                    Comment = Name + ", enter",
                    ExpiryDate = DateTime.Now.AddHours(1),
                    Portfolio = Portfolio,
                    Security = security,
                    Type = OrderTypes.Limit,
                    Volume = SecurityVolumeDictionary[security.Code],
                    Direction = Sides.Buy,
                    Price = security.ShrinkPrice(lastPrice - _delta),
                };

                this.AddInfoLog(
                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                    security.Code, orderBuy.Direction == Sides.Sell ? "продажу" : "покупку",
                    orderBuy.Price.ToString(CultureInfo.InvariantCulture),
                    orderBuy.Volume.ToString(CultureInfo.InvariantCulture),
                    security.ShrinkPrice(Math.Round((lastPrice - _delta) * (1 - StopLossPercent / 100))));

                var orderSell = new Order
                {
                    Comment = Name + ", enter",
                    ExpiryDate = DateTime.Now.AddHours(1),
                    Portfolio = Portfolio,
                    Security = security,
                    Type = OrderTypes.Limit,
                    Volume = SecurityVolumeDictionary[security.Code],
                    Direction = Sides.Sell,
                    Price = security.ShrinkPrice(lastPrice + _delta),
                };

                this.AddInfoLog(
                    "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                    security.Code, orderSell.Direction == Sides.Sell ? "продажу" : "покупку",
                    orderSell.Price.ToString(CultureInfo.InvariantCulture),
                    orderSell.Volume.ToString(CultureInfo.InvariantCulture),
                    security.ShrinkPrice(Math.Round((lastPrice + _delta) * (1 + StopLossPercent / 100))));

                // Регистрируем заявки
                RegisterOrder(orderBuy);
                RegisterOrder(orderSell);
            }
        }

        /// <summary>
        /// Обработчик события прихода ПЕРЕД новой свечкой
        /// </summary>
        void SpikesTimeFrameComeBefore(object sender, TimeFrameEventBeforeArgs e)
        {
            // Если стратегия не запущена, то ничего не делаем
            if(ProcessState != ProcessStates.Started)
                return;

            var currTime = e.MarketTime.Minute + e.MarketTime.Hour * 60;

            this.AddInfoLog("Таймфрейм перед...");

            foreach (var security in SecurityList.Where(security => Orders != null))
            {
                if (Orders.Any())
                {
                    var tempSecurity = security;
                    foreach (var order in Orders.Where(order => order.Security == tempSecurity).Where(order => order.State == OrderStates.Active && order.Comment.EndsWith("enterF")).Where(order => order != null))
                    {
                        Connector.CancelOrder(order);
                    }
                }

                if(IsEveningOnly)
                {
                    // НЕ пытаемся выставить заявки, если не попали во временной фильтр или, если находимся в позиции
                    if (IsWorkContour)
                        if (currTime < 1120 || currTime > 1150 || ActiveTrades.Count(trade => trade.Security == security.Code) != 0)
                            continue;
                    if (!IsWorkContour)
                        if (currTime < 990 || currTime > 1005 || GetCurrentPosition(security) != 0 || security.Code.StartsWith(_singleFarCode))
                            continue;
                }
                else
                {
                    // НЕ пытаемся выставить заявки  позже 23:30 или если есть активные трейды по соответствующему инструменту
                    if (currTime > 1420 || ActiveTrades.Count(trade => trade.Security == security.Code) != 0) continue;
                }

                // Вычисляем дельту от цены последней сделки
                var lastPrice = security.LastTrade.Price;

                _deltaFar = Math.Round(lastPrice * _deltaFarPercent / 100, 0);

                var orderBuyFar = new Order
                {
                    Comment = Name + ", enterF",
                    ExpiryDate = DateTime.Now.AddHours(1),
                    Portfolio = Portfolio,
                    Security = security,
                    Type = OrderTypes.Limit,
                    Volume = _securityVolumeDictionaryPlus[security.Code],
                    Direction = Sides.Buy,
                    Price = security.ShrinkPrice(lastPrice - _deltaFar),
                };

                this.AddInfoLog(
                    "ЗАЯВКА на ВХОД - {0}. Регистрируем дальнюю заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                    security.Code, orderBuyFar.Direction == Sides.Sell ? "продажу" : "покупку",
                    orderBuyFar.Price.ToString(CultureInfo.InvariantCulture),
                    orderBuyFar.Volume.ToString(CultureInfo.InvariantCulture),
                    security.ShrinkPrice(Math.Round((lastPrice - _deltaFar) * (1 - StopLossPercent / 100))));

                var orderSellFar = new Order
                {
                    Comment = Name + ", enterF",
                    ExpiryDate = DateTime.Now.AddHours(1),
                    Portfolio = Portfolio,
                    Security = security,
                    Type = OrderTypes.Limit,
                    Volume = _securityVolumeDictionaryPlus[security.Code],
                    Direction = Sides.Sell,
                    Price = security.ShrinkPrice(lastPrice + _deltaFar),
                };

                this.AddInfoLog(
                    "ЗАЯВКА на ВХОД - {0}. Регистрируем дальнюю заявку на {1} по цене {2} c объемом {3} - стоп на {4}",
                    security.Code, orderSellFar.Direction == Sides.Sell ? "продажу" : "покупку",
                    orderSellFar.Price.ToString(CultureInfo.InvariantCulture),
                    orderSellFar.Volume.ToString(CultureInfo.InvariantCulture),
                    security.ShrinkPrice(Math.Round((lastPrice + _deltaFar) * (1 + StopLossPercent / 100))));

                // Регистрируем заявки
                RegisterOrder(orderBuyFar);
                RegisterOrder(orderSellFar);
            }
        }
        
    }
}
