#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// =====================================================================
//  OrderFlowFollower — NinjaTrader 8 strategy
//  Port of the Python order-flow bot (orderflow/engine.py, strategy.py,
//  risk.py). Same logic, same default parameters.
//
//  Reads every trade with the bid/ask at that moment (OnMarketData), works
//  out who was the aggressor, merges sweep fills into one print, and builds
//  its own footprint bars: delta, large prints, stacked imbalance,
//  absorption, session VWAP. Enters when big prints, delta and footprint
//  structure all agree.
//
//  PROP-FIRM RULES (group "0. Prop firm rules") - enter your firm's numbers:
//  account size, profit target ($, 0 = funded), max drawdown ($) as static /
//  end-of-day trailing / intraday trailing with optional lock, daily loss
//  limit, consistency %, contract cap. The bot stops a safety buffer BEFORE
//  any firm limit and never opens a trade whose stop-out could breach one.
//  Bot protection on top: profit lock, losing-streak cooldown, adaptive risk.
//  All times are US Eastern (daylight saving handled automatically).
//
//  SETUP (see NINJATRADER_SETUP.md):
//   * Chart / Strategy Analyzer data series: MGC 1 Tick, Tick Replay ON
//   * Optional FlowInstrument (e.g. "GC 12-26") to read flow from full-size
//     gold while trading micro gold on the primary series
// =====================================================================
namespace NinjaTrader.NinjaScript.Strategies
{
    public enum OFDrawdownType
    {
        Static,
        EndOfDayTrailing,
        IntradayTrailing
    }

    public enum OFProfile
    {
        Evaluation,   // pass fast: bigger size, looser profit lock, shorter cooldown
        Funded,       // keep the account: smaller size, tighter daily stop, 3-loss cooldown
        Custom        // use the "5. Bot risk" values exactly as entered
    }

    public class OrderFlowFollower : Strategy
    {
        #region Internal types
        private class FlowEvent
        {
            public DateTime T; public long Signed;
            public FlowEvent(DateTime t, long s) { T = t; Signed = s; }
        }

        private class LargePrint
        {
            public DateTime T; public int Side; public long Size;
            public LargePrint(DateTime t, int side, long size) { T = t; Side = side; Size = size; }
        }

        private class FootBar
        {
            public DateTime Open, End;
            public double O, H, L, C;
            public long Vol, Buy, Sell;
            public Dictionary<int, long[]> Levels = new Dictionary<int, long[]>();   // [sellAggr, buyAggr]
        }

        private class Feat
        {
            public double Close, Vwap, Atr;
            public long Delta, RollDelta, LargeNet;
            public int StackedBuy, StackedSell;
            public bool AbsorbBull, AbsorbBear;
        }
        #endregion

        #region State
        // engine
        private Queue<long> sizes;          private long sizeSum;
        private Queue<FlowEvent> deltaQ;    private long rollDelta;
        private Queue<LargePrint> largeQ;   private long largeBuy, largeSell;
        private long cumDelta;              private double pv; private long vwapVol;
        private long sessionId = long.MinValue;
        private Queue<double> ranges;
        private FootBar bar;
        private bool aggPending; private DateTime aggT; private int aggSide; private long aggSize; private double aggPrice;
        private double lastFlowPrice; private int lastTickSide = 1;
        private int flowIdx;
        private long nLarge;

        // strategy
        private int cooldown;
        private bool entryPending; private DateTime entryPendingSince;

        // position accounting (own, independent of commission templates)
        private int posSide;                // +1 long, -1 short, 0 flat
        private int open1, open2;           // contracts open per entry signal
        private double avgEntry;            // average entry price
        private int filledEntryQty;
        private double realized;            // closed P&L incl. commission, all trades
        private double tradePnl;            // P&L of the current trade
        private DateTime entryTimeEt;
        private double lastExecPrice;
        private double pointValue, tickValue;

        // prop-firm rules
        private double firmFloor, firmPeak, lockLevel, dailyAllow, dailyCap;
        private int maxC;
        // bot risk
        private double initialBal, peakEq, lockFloor;
        private DateTime lockUntil = DateTime.MinValue;
        private int streak; private DateTime streakUntil = DateTime.MinValue;
        private string dead = "";
        private long riskDay = long.MinValue; private double dayStart; private bool dayHalt;
        private TimeZoneInfo etZone;
        private int signals, skipped;
        private bool warnedNoReplay;
        private DateTime lastTickEt = DateTime.MinValue;
        private bool exitRequested;
        private long sideCheckBuys, sideCheckTotal; private bool tickRuleForced;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Follows large aggressive traders using footprint order flow, with prop-firm risk protection.";
                Name = "OrderFlowFollower";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 2;
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 60;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 0;
                IsInstantiatedOnEachOptimizationIteration = true;
                TraceOrders = false;

                FlowInstrument = "";
                UseTickRuleOnly = false;
                CommissionPerSide = 0.85;

                Profile = OFProfile.Evaluation;
                AccountSize = 50000;
                ProfitTarget = 3000;
                MaxDrawdown = 2000;
                DrawdownType = OFDrawdownType.IntradayTrailing;
                TrailStopsAtStart = true;
                TrailLockOffset = 0;
                DailyLossLimit = 1000;
                MaxContractsFirm = 50;
                ConsistencyPct = 50;
                DailyProfitCap = 0;
                SafetyBuffer = 200;
                BotDailyStop = 0;

                DayResetEt = 1800;
                TradeStartEt = 830;
                TradeEndEt = 1500;
                FlattenEt = 1600;

                BarSeconds = 60;
                SizeWindow = 2000;
                LargePrintMult = 6.0;
                LargePrintMin = 10;
                FlowWindowSec = 300;
                ImbalanceRatio = 3.0;
                ImbalanceMinVol = 5;
                AbsorptionMult = 4.0;
                AbsorptionHold = 3;
                AtrBars = 14;

                MinNetLarge = 40;
                RequireDeltaConfirm = true;
                RequireStructure = true;
                MinStacked = 3;
                UseVwapFilter = true;
                MaxVwapExtensionAtr = 3.0;
                SlTicks = 30;
                Tp1Ticks = 30;
                Tp2Ticks = 60;
                Tp1Fraction = 0.5;
                BreakevenOffsetTicks = 2;
                MaxHoldMinutes = 30;
                FlowReversalExit = true;
                ReversalFrac = 0.75;
                CooldownBars = 3;

                RiskPctOfRoom = 12.0;
                MaxContracts = 10;
                UseProfitLock = true;
                ProfitLockActivate = 500;
                ProfitLockGivebackPct = 35.0;
                ProfitLockPauseHours = 24.0;
                RelockBuffer = 250;
                UseStreakCooldown = true;
                MaxConsecutiveLosses = 4;
                StreakCooldownHours = 48.0;
                UseAdaptiveRisk = true;
                RiskCutPerLoss = 0.70;
                MinRiskMult = 0.33;
            }
            else if (State == State.Configure)
            {
                if (!string.IsNullOrEmpty(FlowInstrument))
                    AddDataSeries(FlowInstrument, BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                flowIdx = string.IsNullOrEmpty(FlowInstrument) ? 0 : 1;
                sizes = new Queue<long>();
                deltaQ = new Queue<FlowEvent>();
                largeQ = new Queue<LargePrint>();
                ranges = new Queue<double>();
                pointValue = Instrument.MasterInstrument.PointValue;
                tickValue = pointValue * TickSize;
                ApplyProfile();
                initialBal = AccountSize;
                peakEq = AccountSize;
                firmFloor = AccountSize - MaxDrawdown;
                firmPeak = AccountSize;
                lockLevel = TrailStopsAtStart ? AccountSize + TrailLockOffset : double.MaxValue;
                double a1 = DailyLossLimit > 0 ? DailyLossLimit - SafetyBuffer : 0;
                double a2 = BotDailyStop;
                if (a1 > 0 && a2 > 0) dailyAllow = Math.Min(a1, a2);
                else if (a1 > 0) dailyAllow = a1;
                else if (a2 > 0) dailyAllow = a2;
                else dailyAllow = 0.5 * MaxDrawdown;
                dailyCap = DailyProfitCap;
                if (ProfitTarget > 0 && ConsistencyPct > 0)
                {
                    double cons = ConsistencyPct / 100.0 * ProfitTarget * 0.9;   // 10% margin under the rule
                    dailyCap = dailyCap > 0 ? Math.Min(dailyCap, cons) : cons;
                }
                maxC = Math.Min(MaxContracts, MaxContractsFirm);
                try { etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                Print(string.Format("[OFF] Rules: account {0:F0} | target {1:F0} | drawdown {2:F0} {3} | floor {4:F0} | daily allowance {5:F0} | daily cap {6:F0} | contracts <= {7}",
                    AccountSize, ProfitTarget, MaxDrawdown, DrawdownType, firmFloor, dailyAllow, dailyCap, maxC));
                Print(string.Format("[OFF] {0} trading {1} | flow from {2} | tick value {3:F2}",
                    Name, Instrument.FullName, BarsArray[flowIdx].Instrument.FullName, tickValue));
            }
            else if (State == State.Terminated)
            {
                if (initialBal > 0)
                    Print(string.Format("[OFF] Done. Outcome: {0} | realized {1:F2} | signals {2} | skipped (size) {3} | large prints {4}",
                        dead == "" ? "RUNNING" : dead, realized, signals, skipped, nLarge));
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0 && CurrentBars[0] >= 0)
                lastExecPrice = Closes[0][0];

            if (!warnedNoReplay && State == State.Historical && BarsInProgress == 0 && CurrentBars[0] > 50 && nLarge == 0 && sizeSum == 0)
            {
                warnedNoReplay = true;
                Print("[OFF] WARNING: no trades reaching OnMarketData in history. Enable Tick Replay on the data series (Tools > Options > Market Data > Show Tick Replay).");
            }
        }

        // =================================================================
        //  TAPE
        // =================================================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != flowIdx || e.MarketDataType != MarketDataType.Last)
                return;
            if (sizes == null)
                return;

            DateTime t = ToEt(e.Time);
            lastTickEt = t;
            double price = e.Price;
            long size = e.Volume;
            int side = Aggressor(e);
            if (flowIdx == 0)
                lastExecPrice = price;

            if (aggPending && (t != aggT || side != aggSide))
            {
                OnPrint(aggT, aggPrice, aggSize, aggSide);
                aggPending = false;
            }
            if (bar != null && t >= bar.End)
                CloseBar(t);
            if (!aggPending)
            {
                aggPending = true; aggT = t; aggSide = side; aggSize = size; aggPrice = price;
            }
            else
            {
                aggSize += size; aggPrice = price;
            }

            RiskTick(t);
        }

        private int Aggressor(MarketDataEventArgs e)
        {
            int s;
            bool quoteOk = !UseTickRuleOnly && !tickRuleForced && e.Ask > 0 && e.Bid > 0;
            if (quoteOk && e.Price >= e.Ask) s = 1;
            else if (quoteOk && e.Price <= e.Bid) s = -1;
            else if (lastFlowPrice > 0 && e.Price > lastFlowPrice) s = 1;     // tick rule fallback
            else if (lastFlowPrice > 0 && e.Price < lastFlowPrice) s = -1;
            else s = lastTickSide;
            lastFlowPrice = e.Price;
            lastTickSide = s;

            // If history has no real bid/ask, NinjaTrader sets Bid = Last, so every
            // trade reads as a sell. Detect that in the first prints and fall back.
            if (!tickRuleForced && !UseTickRuleOnly && sideCheckTotal < 3000)
            {
                sideCheckTotal++;
                if (s > 0) sideCheckBuys++;
                if (sideCheckTotal == 3000 && sideCheckBuys < 60)
                {
                    tickRuleForced = true;
                    Print("[OFF] WARNING: almost no buy-side aggressors detected - this data has no historical bid/ask. "
                        + "Switched to tick-rule classification (less accurate). Use a feed with tick bid/ask history for real order flow.");
                }
            }
            return s;
        }

        private void OnPrint(DateTime t, double price, long size, int side)
        {
            long sess = SessionOf(t);
            if (sess != sessionId)
            {
                sessionId = sess; cumDelta = 0; pv = 0; vwapVol = 0;
            }

            int n = sizes.Count;
            double mean = n > 0 ? (double)sizeSum / n : size;
            bool isLarge = n >= 100 && size >= LargePrintMin && size >= LargePrintMult * mean;

            sizes.Enqueue(size); sizeSum += size;
            if (sizes.Count > SizeWindow) sizeSum -= sizes.Dequeue();

            long signed = size * side;
            cumDelta += signed;
            rollDelta += signed;
            deltaQ.Enqueue(new FlowEvent(t, signed));
            if (isLarge)
            {
                nLarge++;
                largeQ.Enqueue(new LargePrint(t, side, size));
                if (side > 0) largeBuy += size; else largeSell += size;
            }

            DateTime cutoff = t.AddSeconds(-FlowWindowSec);
            while (deltaQ.Count > 0 && deltaQ.Peek().T < cutoff)
                rollDelta -= deltaQ.Dequeue().Signed;
            while (largeQ.Count > 0 && largeQ.Peek().T < cutoff)
            {
                LargePrint lp = largeQ.Dequeue();
                if (lp.Side > 0) largeBuy -= lp.Size; else largeSell -= lp.Size;
            }

            pv += price * size; vwapVol += size;

            if (bar == null || t >= bar.End)
            {
                if (bar != null) CloseBar(t);
                long secs = (t.Ticks / TimeSpan.TicksPerSecond) / BarSeconds * BarSeconds;
                bar = new FootBar();
                bar.Open = new DateTime(secs * TimeSpan.TicksPerSecond);
                bar.End = bar.Open.AddSeconds(BarSeconds);
                bar.O = bar.H = bar.L = bar.C = price;
            }

            if (price > bar.H) bar.H = price;
            if (price < bar.L) bar.L = price;
            bar.C = price;
            bar.Vol += size;
            int k = (int)Math.Round(price / TickSize);
            long[] lv;
            if (!bar.Levels.TryGetValue(k, out lv)) { lv = new long[2]; bar.Levels[k] = lv; }
            if (side > 0) { bar.Buy += size; lv[1] += size; }
            else { bar.Sell += size; lv[0] += size; }
        }

        private void CloseBar(DateTime now)
        {
            FootBar b = bar;
            bar = null;
            ranges.Enqueue(b.H - b.L);
            while (ranges.Count > AtrBars) ranges.Dequeue();
            double atr = 0; foreach (double r in ranges) atr += r; atr /= ranges.Count;

            List<int> keys = new List<int>(b.Levels.Keys);
            keys.Sort();
            int bestB = 0, runB = 0, bestS = 0, runS = 0, prev = int.MinValue;
            foreach (int k in keys)
            {
                if (prev != int.MinValue && k != prev + 1) { runB = 0; runS = 0; }
                long sellK = b.Levels[k][0], buyK = b.Levels[k][1];
                long belowSell = b.Levels.ContainsKey(k - 1) ? b.Levels[k - 1][0] : 0;
                long aboveBuy = b.Levels.ContainsKey(k + 1) ? b.Levels[k + 1][1] : 0;
                runB = (buyK >= ImbalanceMinVol && buyK >= ImbalanceRatio * Math.Max(belowSell, 1)) ? runB + 1 : 0;
                runS = (sellK >= ImbalanceMinVol && sellK >= ImbalanceRatio * Math.Max(aboveBuy, 1)) ? runS + 1 : 0;
                bestB = Math.Max(bestB, runB); bestS = Math.Max(bestS, runS);
                prev = k;
            }

            bool absBull = false, absBear = false;
            if (keys.Count >= 4 && b.Vol > 0)
            {
                double avg = (double)b.Vol / keys.Count;
                int lo = keys[0], hi = keys[keys.Count - 1];
                int ck = (int)Math.Round(b.C / TickSize);
                long sellLo = LevelVol(b, lo, 0) + LevelVol(b, lo + 1, 0);
                long buyLo  = LevelVol(b, lo, 1) + LevelVol(b, lo + 1, 1);
                long buyHi  = LevelVol(b, hi, 1) + LevelVol(b, hi - 1, 1);
                long sellHi = LevelVol(b, hi, 0) + LevelVol(b, hi - 1, 0);
                absBull = sellLo >= AbsorptionMult * avg && sellLo > 2 * buyLo && ck >= lo + AbsorptionHold;
                absBear = buyHi >= AbsorptionMult * avg && buyHi > 2 * sellHi && ck <= hi - AbsorptionHold;
            }

            Feat f = new Feat();
            f.Close = b.C;
            f.Vwap = vwapVol > 0 ? pv / vwapVol : b.C;
            f.Atr = atr;
            f.Delta = b.Buy - b.Sell;
            f.RollDelta = rollDelta;
            f.LargeNet = largeBuy - largeSell;
            f.StackedBuy = bestB; f.StackedSell = bestS;
            f.AbsorbBull = absBull; f.AbsorbBear = absBear;
            OnFeatures(f, now);
        }

        private static long LevelVol(FootBar b, int k, int i)
        {
            long[] lv;
            return b.Levels.TryGetValue(k, out lv) ? lv[i] : 0;
        }

        // =================================================================
        //  STRATEGY
        // =================================================================
        private void OnFeatures(Feat f, DateTime t)
        {
            if (posSide != 0)
            {
                bool rev = false;
                if (FlowReversalExit)
                {
                    double thr = MinNetLarge * ReversalFrac;
                    rev = posSide > 0 ? (f.LargeNet <= -thr && f.RollDelta < 0)
                                      : (f.LargeNet >= thr && f.RollDelta > 0);
                }
                if (rev) ExitAll("FlowReversal");
                else if ((t - entryTimeEt).TotalMinutes >= MaxHoldMinutes) ExitAll("TimeStop");
                return;
            }
            if (entryPending)
            {
                if ((t - entryPendingSince).TotalSeconds < 30) return;
                entryPending = false;   // entry never filled — release
            }
            int mod = t.Hour * 60 + t.Minute;
            if (mod < Minutes(TradeStartEt) || mod >= Minutes(TradeEndEt)) return;
            if (!CanTrade(t)) return;

            int sig = EntrySignal(f);
            if (sig == 0) return;
            signals++;

            double perContract = SlTicks * tickValue + 2 * CommissionPerSide + 2 * tickValue;
            int qty = Contracts(perContract);
            if (qty < 1) { skipped++; return; }

            int q1 = qty >= 2 ? Math.Max(1, (int)(qty * Tp1Fraction)) : qty;
            int q2 = qty - q1;
            SetStopLoss("OF1", CalculationMode.Ticks, SlTicks, false);
            SetProfitTarget("OF1", CalculationMode.Ticks, Tp1Ticks);
            if (q2 > 0)
            {
                SetStopLoss("OF2", CalculationMode.Ticks, SlTicks, false);
                SetProfitTarget("OF2", CalculationMode.Ticks, Tp2Ticks);
            }
            if (sig > 0)
            {
                EnterLong(0, q1, "OF1");
                if (q2 > 0) EnterLong(0, q2, "OF2");
            }
            else
            {
                EnterShort(0, q1, "OF1");
                if (q2 > 0) EnterShort(0, q2, "OF2");
            }
            entryPending = true;
            entryPendingSince = t;
            entryTimeEt = t;
            Print(string.Format("[OFF] {0} {1} SIGNAL {2}x | largeNet {3} rollDelta {4} stacked {5}/{6} absorb {7}/{8} vwap {9:F2}",
                t, sig > 0 ? "LONG" : "SHORT", qty, f.LargeNet, f.RollDelta, f.StackedBuy, f.StackedSell,
                f.AbsorbBull, f.AbsorbBear, f.Vwap));
        }

        private int EntrySignal(Feat f)
        {
            if (cooldown > 0) { cooldown--; return 0; }
            if (f.Atr <= 0) return 0;

            bool lg = f.LargeNet >= MinNetLarge;
            bool sh = f.LargeNet <= -MinNetLarge;
            if (RequireDeltaConfirm)
            {
                lg = lg && f.RollDelta > 0 && f.Delta > 0;
                sh = sh && f.RollDelta < 0 && f.Delta < 0;
            }
            if (RequireStructure)
            {
                lg = lg && (f.StackedBuy >= MinStacked || f.AbsorbBull);
                sh = sh && (f.StackedSell >= MinStacked || f.AbsorbBear);
            }
            if (UseVwapFilter)
            {
                double ext = MaxVwapExtensionAtr * f.Atr;
                lg = lg && f.Close > f.Vwap && f.Close <= f.Vwap + ext;
                sh = sh && f.Close < f.Vwap && f.Close >= f.Vwap - ext;
            }
            if (lg && !sh) return 1;
            if (sh && !lg) return -1;
            return 0;
        }

        private void ExitAll(string reason)
        {
            if (exitRequested || posSide == 0) return;
            exitRequested = true;
            if (posSide > 0)
            {
                if (open1 > 0) ExitLong(0, open1, reason, "OF1");
                if (open2 > 0) ExitLong(0, open2, reason, "OF2");
            }
            else if (posSide < 0)
            {
                if (open1 > 0) ExitShort(0, open1, reason, "OF1");
                if (open2 > 0) ExitShort(0, open2, reason, "OF2");
            }
        }

        // =================================================================
        //  EXECUTIONS — own P&L accounting, breakeven after TP1
        // =================================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null || execution.Instrument != Instruments[0]) return;
            Order o = execution.Order;
            bool isEntry = o.Name == "OF1" || o.Name == "OF2";
            double comm = CommissionPerSide * quantity;

            if (isEntry)
            {
                int side = (o.OrderAction == OrderAction.Buy) ? 1 : -1;
                if (posSide == 0) { posSide = side; tradePnl = 0; filledEntryQty = 0; avgEntry = 0; exitRequested = false; }
                avgEntry = (avgEntry * filledEntryQty + price * quantity) / (filledEntryQty + quantity);
                filledEntryQty += quantity;
                if (o.Name == "OF1") open1 += quantity; else open2 += quantity;
                realized -= comm; tradePnl -= comm;
                entryPending = false;
                return;
            }

            if (posSide == 0) return;
            double pnl = (price - avgEntry) * posSide * quantity * pointValue - comm;
            realized += pnl; tradePnl += pnl;
            if (o.FromEntrySignal == "OF1") open1 = Math.Max(0, open1 - quantity);
            else if (o.FromEntrySignal == "OF2") open2 = Math.Max(0, open2 - quantity);
            else { open1 = 0; open2 = 0; }

            if (o.Name == "Profit target" && o.FromEntrySignal == "OF1" && open2 > 0)
            {
                double be = avgEntry + posSide * BreakevenOffsetTicks * TickSize;
                SetStopLoss("OF2", CalculationMode.Price, be, false);
            }

            if (open1 + open2 == 0)
            {
                OnTradeClosed(lastTickEt, tradePnl);
                posSide = 0;
                exitRequested = false;
                cooldown = CooldownBars;
            }
        }


        // =================================================================
        //  RISK
        // =================================================================
        private void RiskTick(DateTime t)
        {
            if (dead != "") return;
            double balance = initialBal + realized;
            double eq = Equity();

            long day = SessionOf(t);
            if (day != riskDay)
            {
                if (riskDay != long.MinValue && DrawdownType == OFDrawdownType.EndOfDayTrailing)
                    RaiseFloor(balance);
                riskDay = day; dayStart = balance; dayHalt = false;
            }
            if (DrawdownType == OFDrawdownType.IntradayTrailing && eq > firmPeak)
            {
                firmPeak = eq;
                RaiseFloor(eq);
            }

            string why = "";
            double dayPnl = eq - dayStart;
            if (eq <= firmFloor + SafetyBuffer)
            { dead = "DRAWDOWN_GUARD"; why = string.Format("DRAWDOWN GUARD at equity {0:F2} - firm floor {1:F2}. Permanent stop.", eq, firmFloor); }
            else if (ProfitTarget > 0 && eq >= initialBal + ProfitTarget + 0.25 * SafetyBuffer)   // margin for closing slippage
            { dead = "PASSED"; why = string.Format("PROFIT TARGET reached at equity {0:F2} - evaluation passed", eq); }
            else if (!dayHalt && dayPnl <= -dailyAllow)
            { dayHalt = true; why = string.Format("DAILY STOP at day P&L {0:F2} (allowed -{1:F0})", dayPnl, dailyAllow); }
            else if (!dayHalt && dailyCap > 0 && dayPnl >= dailyCap)
            { dayHalt = true; why = string.Format("DAILY PROFIT CAP {0:F2} >= {1:F0} (consistency) - done for today", dayPnl, dailyCap); }
            else if (UseProfitLock)
            {
                if (eq > peakEq) peakEq = eq;
                double pp = peakEq - initialBal;
                if (pp >= ProfitLockActivate)
                    lockFloor = Math.Max(lockFloor, initialBal + pp * (1 - ProfitLockGivebackPct / 100));
                if (lockFloor > 0 && eq < lockFloor && t >= lockUntil)
                {
                    lockUntil = t.AddHours(ProfitLockPauseHours);
                    why = string.Format("PROFIT LOCK at equity {0:F2} (lock {1:F2}) - pausing {2:F0}h", eq, lockFloor, ProfitLockPauseHours);
                    lockFloor = eq - RelockBuffer;   // re-anchor so trading can resume
                    peakEq = eq;
                }
            }

            if (why != "")
            {
                Print("[OFF] " + t + " ET  " + why);
                ExitAll("Risk");
            }
            else if (posSide != 0)
            {
                int mod = t.Hour * 60 + t.Minute;
                if (mod >= Minutes(FlattenEt) && mod < Minutes(DayResetEt))
                    ExitAll("SessionEnd");
            }
        }

        private double Equity()
        {
            double unreal = posSide != 0 && lastExecPrice > 0
                ? (lastExecPrice - avgEntry) * posSide * (open1 + open2) * pointValue : 0;
            return initialBal + realized + unreal;
        }

        private void RaiseFloor(double level)
        {
            double cand = Math.Min(level - MaxDrawdown, lockLevel);
            if (cand > firmFloor) firmFloor = cand;
        }

        private bool CanTrade(DateTime t)
        {
            if (dead != "" || dayHalt || t < lockUntil) return false;
            if (streakUntil != DateTime.MinValue)
            {
                if (t < streakUntil) return false;
                streakUntil = DateTime.MinValue; streak = 0;
                Print("[OFF] " + t + "  Streak cooldown over - trading resumes");
            }
            return true;
        }

        private void OnTradeClosed(DateTime t, double pnl)
        {
            double balance = initialBal + realized;
            if (pnl < -0.01 * MaxDrawdown)
            {
                streak++;
                if (UseStreakCooldown && streak >= MaxConsecutiveLosses && streakUntil == DateTime.MinValue)
                {
                    streakUntil = t.AddHours(StreakCooldownHours);
                    Print(string.Format("[OFF] {0}  {1} losses in a row - cooling down {2:F0}h", t, streak, StreakCooldownHours));
                }
            }
            else if (pnl > 0.005 * MaxDrawdown)
                streak = 0;
            Print(string.Format("[OFF] {0} ET  trade closed {1:F2} | balance {2:F2} | floor {3:F2} | streak {4}", t, pnl, balance, firmFloor, streak));
        }

        private int Contracts(double perContract)
        {
            if (perContract <= 0) return 0;
            double eq = Equity();
            double room = eq - (firmFloor + SafetyBuffer);
            if (room <= 0) return 0;
            double mult = 1.0;
            if (UseAdaptiveRisk && streak > 0)
                mult = Math.Max(MinRiskMult, Math.Pow(RiskCutPerLoss, streak));
            double baseRisk = room * RiskPctOfRoom / 100;
            int n = (int)Math.Floor(baseRisk * mult / perContract);
            if (n < 1 && (int)Math.Floor(baseRisk / perContract) >= 1)
                n = 1;   // adaptive risk never shrinks to zero (would freeze the bot)
            n = Math.Min(n, maxC);
            double dayPnl = eq - dayStart;
            while (n > 0 && (eq - n * perContract <= firmFloor + SafetyBuffer || dayPnl - n * perContract <= -dailyAllow))
                n--;   // never open a trade whose stop-out could breach a firm limit
            return n;
        }

        // =================================================================
        //  HELPERS
        // =================================================================
        // Profiles set only the bot's own risk knobs. Firm rules are never changed.
        private void ApplyProfile()
        {
            if (Profile == OFProfile.Evaluation)
            {
                RiskPctOfRoom = 20.0; MaxContracts = 15;
                UseProfitLock = true; ProfitLockActivate = 0.5 * MaxDrawdown; ProfitLockGivebackPct = 50; ProfitLockPauseHours = 12; RelockBuffer = 0.2 * MaxDrawdown;
                UseStreakCooldown = true; MaxConsecutiveLosses = 4; StreakCooldownHours = 24;
                UseAdaptiveRisk = true; RiskCutPerLoss = 0.75; MinRiskMult = 0.40;
                CooldownBars = 2;
            }
            else if (Profile == OFProfile.Funded)
            {
                RiskPctOfRoom = 8.0; MaxContracts = 8;
                UseProfitLock = true; ProfitLockActivate = 0.2 * MaxDrawdown; ProfitLockGivebackPct = 35; ProfitLockPauseHours = 24; RelockBuffer = 0.1 * MaxDrawdown;
                UseStreakCooldown = true; MaxConsecutiveLosses = 3; StreakCooldownHours = 48;
                UseAdaptiveRisk = true; RiskCutPerLoss = 0.70; MinRiskMult = 0.33;
                CooldownBars = 3;
                BotDailyStop = 0.20 * MaxDrawdown;
                if (ProfitTarget > 0)
                    Print("[OFF] WARNING: Funded profile with a profit target set - the bot will STOP at the target. Set Profit target = 0 for a funded account.");
            }
            Print(string.Format("[OFF] Profile {0}: risk {1}% of room, max {2} contracts, cooldown after {3} losses for {4}h",
                Profile, RiskPctOfRoom, MaxContracts, MaxConsecutiveLosses, StreakCooldownHours));
        }

        private DateTime ToEt(DateTime platformTime)
        {
            try
            {
                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified),
                                                NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, etZone);
            }
            catch
            {
                return platformTime;
            }
        }

        private static int Minutes(int hhmm)
        {
            return (hhmm / 100) * 60 + hhmm % 100;
        }

        private long SessionOf(DateTime et)
        {
            // CME trading day starts at DayResetEt (18:00): evening hours belong to the next day
            return et.AddMinutes(1440 - Minutes(DayResetEt)).Date.Ticks / TimeSpan.TicksPerDay;
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Flow instrument (blank = chart)", Order = 1, GroupName = "1. Instrument")]
        public string FlowInstrument { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Aggressor by tick rule only", Order = 4, GroupName = "1. Instrument")]
        public bool UseTickRuleOnly { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Commission per side ($)", Order = 3, GroupName = "1. Instrument")]
        public double CommissionPerSide { get; set; }

        // ---------------- 0. Prop firm rules ----------------
        [NinjaScriptProperty]
        [Display(Name = "Profile (Evaluation / Funded / Custom)", Order = 0, GroupName = "0. Prop firm rules")]
        public OFProfile Profile { get; set; }

        [NinjaScriptProperty][Range(1000, double.MaxValue)]
        [Display(Name = "Account size ($)", Order = 1, GroupName = "0. Prop firm rules")]
        public double AccountSize { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Profit target ($, 0 = funded)", Order = 2, GroupName = "0. Prop firm rules")]
        public double ProfitTarget { get; set; }

        [NinjaScriptProperty][Range(1, double.MaxValue)]
        [Display(Name = "Max drawdown ($)", Order = 3, GroupName = "0. Prop firm rules")]
        public double MaxDrawdown { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Drawdown type", Order = 4, GroupName = "0. Prop firm rules")]
        public OFDrawdownType DrawdownType { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trailing stops at start balance", Order = 5, GroupName = "0. Prop firm rules")]
        public bool TrailStopsAtStart { get; set; }

        [NinjaScriptProperty][Range(-100000, 100000)]
        [Display(Name = "Trail lock offset ($ above start)", Order = 6, GroupName = "0. Prop firm rules")]
        public double TrailLockOffset { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Daily loss limit ($, 0 = none)", Order = 7, GroupName = "0. Prop firm rules")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty][Range(1, 10000)]
        [Display(Name = "Firm max contracts (traded contract)", Order = 8, GroupName = "0. Prop firm rules")]
        public int MaxContractsFirm { get; set; }

        [NinjaScriptProperty][Range(0, 100)]
        [Display(Name = "Consistency % (best day vs target, 0 = off)", Order = 9, GroupName = "0. Prop firm rules")]
        public double ConsistencyPct { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Daily profit cap ($, 0 = off)", Order = 10, GroupName = "0. Prop firm rules")]
        public double DailyProfitCap { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Safety buffer ($ before firm limits)", Order = 11, GroupName = "0. Prop firm rules")]
        public double SafetyBuffer { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Bot daily stop ($, 0 = firm limit)", Order = 12, GroupName = "0. Prop firm rules")]
        public double BotDailyStop { get; set; }

        // ---------------- 2. Session (US Eastern) ----------------
        [NinjaScriptProperty][Range(0, 2359)]
        [Display(Name = "Trading day starts (HHMM ET)", Order = 1, GroupName = "2. Session (US Eastern, HHMM)")]
        public int DayResetEt { get; set; }

        [NinjaScriptProperty][Range(0, 2359)]
        [Display(Name = "First entry (HHMM ET)", Order = 2, GroupName = "2. Session (US Eastern, HHMM)")]
        public int TradeStartEt { get; set; }

        [NinjaScriptProperty][Range(0, 2359)]
        [Display(Name = "Last entry (HHMM ET)", Order = 3, GroupName = "2. Session (US Eastern, HHMM)")]
        public int TradeEndEt { get; set; }

        [NinjaScriptProperty][Range(0, 2359)]
        [Display(Name = "Flatten all (HHMM ET)", Order = 4, GroupName = "2. Session (US Eastern, HHMM)")]
        public int FlattenEt { get; set; }

        [NinjaScriptProperty][Range(5, 3600)]
        [Display(Name = "Footprint bar seconds", Order = 1, GroupName = "3. Order flow")]
        public int BarSeconds { get; set; }

        [NinjaScriptProperty][Range(100, 100000)]
        [Display(Name = "Size window (prints)", Order = 2, GroupName = "3. Order flow")]
        public int SizeWindow { get; set; }

        [NinjaScriptProperty][Range(1.0, 100.0)]
        [Display(Name = "Large print x average", Order = 3, GroupName = "3. Order flow")]
        public double LargePrintMult { get; set; }

        [NinjaScriptProperty][Range(1, 10000)]
        [Display(Name = "Large print min contracts", Order = 4, GroupName = "3. Order flow")]
        public int LargePrintMin { get; set; }

        [NinjaScriptProperty][Range(10, 7200)]
        [Display(Name = "Flow window seconds", Order = 5, GroupName = "3. Order flow")]
        public int FlowWindowSec { get; set; }

        [NinjaScriptProperty][Range(1.0, 50.0)]
        [Display(Name = "Imbalance ratio", Order = 6, GroupName = "3. Order flow")]
        public double ImbalanceRatio { get; set; }

        [NinjaScriptProperty][Range(1, 10000)]
        [Display(Name = "Imbalance min volume", Order = 7, GroupName = "3. Order flow")]
        public int ImbalanceMinVol { get; set; }

        [NinjaScriptProperty][Range(1.0, 50.0)]
        [Display(Name = "Absorption x avg level", Order = 8, GroupName = "3. Order flow")]
        public double AbsorptionMult { get; set; }

        [NinjaScriptProperty][Range(1, 100)]
        [Display(Name = "Absorption hold ticks", Order = 9, GroupName = "3. Order flow")]
        public int AbsorptionHold { get; set; }

        [NinjaScriptProperty][Range(1, 200)]
        [Display(Name = "ATR bars", Order = 10, GroupName = "3. Order flow")]
        public int AtrBars { get; set; }

        [NinjaScriptProperty][Range(1, 100000)]
        [Display(Name = "Min net large contracts", Order = 1, GroupName = "4. Entry & exit")]
        public int MinNetLarge { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require delta confirm", Order = 2, GroupName = "4. Entry & exit")]
        public bool RequireDeltaConfirm { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require footprint structure", Order = 3, GroupName = "4. Entry & exit")]
        public bool RequireStructure { get; set; }

        [NinjaScriptProperty][Range(1, 50)]
        [Display(Name = "Min stacked levels", Order = 4, GroupName = "4. Entry & exit")]
        public int MinStacked { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use VWAP filter", Order = 5, GroupName = "4. Entry & exit")]
        public bool UseVwapFilter { get; set; }

        [NinjaScriptProperty][Range(0.1, 50.0)]
        [Display(Name = "Max VWAP extension (ATR)", Order = 6, GroupName = "4. Entry & exit")]
        public double MaxVwapExtensionAtr { get; set; }

        [NinjaScriptProperty][Range(1, 10000)]
        [Display(Name = "Stop loss ticks", Order = 7, GroupName = "4. Entry & exit")]
        public int SlTicks { get; set; }

        [NinjaScriptProperty][Range(1, 10000)]
        [Display(Name = "TP1 ticks", Order = 8, GroupName = "4. Entry & exit")]
        public int Tp1Ticks { get; set; }

        [NinjaScriptProperty][Range(1, 10000)]
        [Display(Name = "TP2 ticks", Order = 9, GroupName = "4. Entry & exit")]
        public int Tp2Ticks { get; set; }

        [NinjaScriptProperty][Range(0.1, 0.9)]
        [Display(Name = "TP1 fraction", Order = 10, GroupName = "4. Entry & exit")]
        public double Tp1Fraction { get; set; }

        [NinjaScriptProperty][Range(0, 100)]
        [Display(Name = "Breakeven offset ticks", Order = 11, GroupName = "4. Entry & exit")]
        public int BreakevenOffsetTicks { get; set; }

        [NinjaScriptProperty][Range(1, 1440)]
        [Display(Name = "Max hold minutes", Order = 12, GroupName = "4. Entry & exit")]
        public int MaxHoldMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Flow reversal exit", Order = 13, GroupName = "4. Entry & exit")]
        public bool FlowReversalExit { get; set; }

        [NinjaScriptProperty][Range(0.1, 5.0)]
        [Display(Name = "Reversal fraction", Order = 14, GroupName = "4. Entry & exit")]
        public double ReversalFrac { get; set; }

        [NinjaScriptProperty][Range(0, 1000)]
        [Display(Name = "Cooldown bars after exit", Order = 15, GroupName = "4. Entry & exit")]
        public int CooldownBars { get; set; }

        [NinjaScriptProperty][Range(0.5, 100.0)]
        [Display(Name = "Risk % of room above floor", Order = 1, GroupName = "5. Bot risk")]
        public double RiskPctOfRoom { get; set; }

        [NinjaScriptProperty][Range(1, 1000)]
        [Display(Name = "Bot max contracts", Order = 2, GroupName = "5. Bot risk")]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use profit lock", Order = 3, GroupName = "5. Bot risk")]
        public bool UseProfitLock { get; set; }

        [NinjaScriptProperty][Range(1, double.MaxValue)]
        [Display(Name = "Profit lock arms at ($ profit)", Order = 4, GroupName = "5. Bot risk")]
        public double ProfitLockActivate { get; set; }

        [NinjaScriptProperty][Range(1.0, 99.0)]
        [Display(Name = "Profit lock giveback %", Order = 5, GroupName = "5. Bot risk")]
        public double ProfitLockGivebackPct { get; set; }

        [NinjaScriptProperty][Range(0.0, 1000.0)]
        [Display(Name = "Profit lock pause hours", Order = 6, GroupName = "5. Bot risk")]
        public double ProfitLockPauseHours { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Relock buffer ($)", Order = 7, GroupName = "5. Bot risk")]
        public double RelockBuffer { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use streak cooldown", Order = 8, GroupName = "5. Bot risk")]
        public bool UseStreakCooldown { get; set; }

        [NinjaScriptProperty][Range(1, 100)]
        [Display(Name = "Max consecutive losses", Order = 9, GroupName = "5. Bot risk")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty][Range(0.0, 2000.0)]
        [Display(Name = "Streak cooldown hours", Order = 10, GroupName = "5. Bot risk")]
        public double StreakCooldownHours { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use adaptive risk", Order = 11, GroupName = "5. Bot risk")]
        public bool UseAdaptiveRisk { get; set; }

        [NinjaScriptProperty][Range(0.1, 1.0)]
        [Display(Name = "Risk cut per loss", Order = 12, GroupName = "5. Bot risk")]
        public double RiskCutPerLoss { get; set; }

        [NinjaScriptProperty][Range(0.05, 1.0)]
        [Display(Name = "Min risk multiplier", Order = 13, GroupName = "5. Bot risk")]
        public double MinRiskMult { get; set; }
        #endregion
    }
}
