using IBApi;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LightSpeedTrader.IBKR
{
    public class IBKRClient : EWrapper
    {
        private readonly EClientSocket _client;
        private readonly EReaderSignal _signal;

        public event Action<string> OnConnectionStatusChanged;
        public event Action<string, string, double, double, double, double, double, double> OnRealtimeBar;
        public event Action<int> OnNextValidId;

        private int _nextReqId = 1;
        private string _currentSymbol = "AAPL";
        private string _currentTimeframe = "1m";

        private TaskCompletionSource<int> _connectedTcs;

        public IBKRClient()
        {
            _signal = new EReaderMonitorSignal();
            _client = new EClientSocket(this, _signal);
        }

        #region Connection
        public Task ConnectAsync(string host, int port, int clientId)
        {
            if (!_client.IsConnected())
            {
                _connectedTcs = new TaskCompletionSource<int>();

                _client.eConnect(host, port, clientId);

                var reader = new EReader(_client, _signal);
                reader.Start();
                new Thread(() =>
                {
                    while (_client.IsConnected())
                    {
                        _signal.waitForSignal();
                        reader.processMsgs();
                    }
                })
                { IsBackground = true }.Start();

                return _connectedTcs.Task;
            }

            return Task.CompletedTask;
        }

        public void nextValidId(int orderId)
        {
            OnNextValidId?.Invoke(orderId);
            _connectedTcs?.TrySetResult(orderId);
        }

        public void Disconnect()
        {
            if (_client.IsConnected())
                _client.eDisconnect();
        }
        #endregion

        #region Market Data
        public void RequestRealtimeBars(string symbol, string timeframe = "1m")
        {
            if (!_client.IsConnected()) return;

            _currentSymbol = symbol;
            _currentTimeframe = timeframe;

            var contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART"
            };

            int reqId = _nextReqId++;
            _client.reqRealTimeBars(reqId, contract, 5, "TRADES", false, null);
        }

        public void RequestHistoricalBars(string symbol, string timeframe)
        {
            if (!_client.IsConnected()) return;

            var contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART"
            };

            int reqId = _nextReqId++;
            string duration = timeframe switch
            {
                "1m" => "1 D",
                "5m" => "5 D",
                "15m" => "15 D",
                _ => "1 D"
            };
            string barSize = timeframe switch
            {
                "1m" => "1 min",
                "5m" => "5 mins",
                "15m" => "15 mins",
                _ => "1 min"
            };

            _client.reqHistoricalData(reqId, contract, "", duration, barSize, "TRADES", 1, 1, false, null);
        }

        #endregion

        #region Orders
        public void PlaceOrder(string symbol, decimal amount, string action)
        {
            if (!_client.IsConnected()) return;

            var contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART"
            };

            var order = new Order
            {
                Action = action,
                OrderType = "MKT",
                TotalQuantity = amount
            };

            int orderId = _nextReqId++;
            _client.placeOrder(orderId, contract, order);
        }
        #endregion

        #region EWrapper implementations
        public void realtimeBar(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal WAP, int count)
        {
            double timeOADate = DateTimeOffset.FromUnixTimeSeconds(date).DateTime.ToOADate();

            OnRealtimeBar?.Invoke(
                _currentSymbol,
                _currentTimeframe,
                timeOADate,
                open,
                high,
                low,
                close,
                (double)volume
            );
        }

        public void connectAck()
        {
            if (_client.AsyncEConnect)
                _client.startApi();
        }

        public void connectionClosed() => OnConnectionStatusChanged?.Invoke("Connection Closed");

        public void error(Exception e) => OnConnectionStatusChanged?.Invoke($"Error: {e.Message}");
        public void error(string str) => OnConnectionStatusChanged?.Invoke($"Error: {str}");
        public void error(int id, int errorCode, string errorMsg) => OnConnectionStatusChanged?.Invoke($"Request {id}, Code {errorCode} - {errorMsg}");

        // --- All other EWrapper methods safely implemented as no-op ---
        public void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson) { }
        public void currentTime(long time) { }
        public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
        public void tickSize(int tickerId, int field, decimal size) { }
        public void tickString(int tickerId, int field, string value) { }
        public void tickGeneric(int tickerId, int field, double value) { }
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints,
                            double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact,
                            double dividendsToLastTradeDate)
        { }
        public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
        public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVolatility,
                                          double delta, double optPrice, double pvDividend, double gamma,
                                          double vega, double theta, double undPrice)
        { }
        public void tickSnapshotEnd(int tickerId) { }
        public void managedAccounts(string accountsList) { }
        public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
        public void accountSummaryEnd(int reqId) { }
        public void bondContractDetails(int reqId, ContractDetails contract) { }
        public void updateAccountValue(string key, string value, string currency, string accountName) { }
        public void updatePortfolio(Contract contract, decimal position, double marketPrice, double marketValue,
                                    double averageCost, double unrealizedPNL, double realizedPNL, string accountName)
        { }
        public void updateAccountTime(string timestamp) { }
        public void accountDownloadEnd(string account) { }
        public void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice,
                                int permId, int parentId, double lastFillPrice, int clientId, string whyHeld,
                                double mktCapPrice)
        { }
        public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
        public void openOrderEnd() { }
        public void contractDetails(int reqId, ContractDetails contractDetails) { }
        public void contractDetailsEnd(int reqId) { }
        public void execDetails(int reqId, Contract contract, Execution execution) { }
        public void execDetailsEnd(int reqId) { }
        public void commissionReport(CommissionReport commissionReport) { }
        public void fundamentalData(int reqId, string data) { }
        public void historicalData(int reqId, Bar bar) { }
        public void historicalDataUpdate(int reqId, Bar bar) { }
        public void historicalDataEnd(int reqId, string start, string end) { }
        public void marketDataType(int reqId, int marketDataType) { }
        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size) { }
        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side,
                                     double price, decimal size, bool isSmartDepth)
        { }
        public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
        public void position(string account, Contract contract, decimal pos, double avgCost) { }
        public void positionEnd() { }
        public void scannerParameters(string xml) { }
        public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance,
                                string benchmark, string projection, string legsStr)
        { }
        public void scannerDataEnd(int reqId) { }
        public void receiveFA(int faDataType, string faXmlData) { }
        public void verifyMessageAPI(string apiData) { }
        public void verifyCompleted(bool isSuccessful, string errorText) { }
        public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
        public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
        public void displayGroupList(int reqId, string groups) { }
        public void displayGroupUpdated(int reqId, string contractInfo) { }
        public void positionMulti(int requestId, string account, string modelCode, Contract contract,
                                  decimal pos, double avgCost)
        { }
        public void positionMultiEnd(int requestId) { }
        public void accountUpdateMulti(int requestId, string account, string modelCode, string key,
                                       string value, string currency)
        { }
        public void accountUpdateMultiEnd(int requestId) { }
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId,
                                                      string tradingClass, string multiplier,
                                                      HashSet<string> expirations, HashSet<double> strikes)
        { }
        public void securityDefinitionOptionParameterEnd(int reqId) { }
        public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
        public void familyCodes(FamilyCode[] familyCodes) { }
        public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
        public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
        public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
        public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
        public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
        public void newsProviders(NewsProvider[] newsProviders) { }
        public void newsArticle(int requestId, int articleType, string articleText) { }
        public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
        public void historicalNewsEnd(int requestId, bool hasMore) { }
        public void headTimestamp(int reqId, string headTimestamp) { }
        public void histogramData(int reqId, HistogramEntry[] data) { }
        public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
        public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
        public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
        public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
        public void pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
        public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
        public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
        public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
        public void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
                                      TickAttribLast tickAttribLast, string exchange, string specialConditions)
        { }
        public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize,
                                     decimal askSize, TickAttribBidAsk tickAttribBidAsk)
        { }
        public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
        public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
        public void completedOrder(Contract contract, Order order, OrderState orderState) { }
        public void completedOrdersEnd() { }
        public void replaceFAEnd(int reqId, string text) { }
        public void wshMetaData(int reqId, string dataJson) { }
        public void wshEventData(int reqId, string dataJson) { }
        public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, HistoricalSession[] sessions) { }
        public void userInfo(int reqId, string whiteBrandingId) { }

        #endregion
    }
}
                                         