# Order-Flow Bot — Gold Futures (NinjaTrader 8)

Follows large traders on the CME gold tape. It reads order flow on **GC**
(full-size gold, where the big participants trade) and can execute on **MGC**
(micro gold, 1/10th the size), so a smaller prop-firm account can follow big
money with sensible risk.

Two parts, same logic:

| Part | What it is |
|---|---|
| `ninjatrader/` | **The trading bot.** `OrderFlowFollower.cs` (NinjaScript strategy) + `OrderFlowTickExporter.cs` (exports NinjaTrader tick data). Start with [`ninjatrader/NINJATRADER_SETUP.md`](ninjatrader/NINJATRADER_SETUP.md). |
| `orderflow/` + scripts | Python research side: fast backtests, multi-run sweeps, charts. Reads Databento data or data exported from NinjaTrader. |

The NinjaTrader strategy uses NinjaTrader's own data connection — no API key needed.

## What it reads from the tape

All computed from raw trades (price, size, bid/ask at that moment) — nothing is
downloaded pre-made:

- **Delta** — aggressive buy volume minus aggressive sell volume (per bar, rolling 5 min, session)
- **Big trades** — prints 6x the average size and at least 10 contracts; fills at the
  same instant are merged, because one sweep arrives as several fills
- **Stacked imbalance** — buyers (or sellers) outnumbering the other side 3:1 across 3+ prices in a row
- **Absorption** — heavy selling at the bar's low (or buying at the high) that failed to move price
- **Session VWAP** on real traded volume

## Entry logic

Long when (short is the mirror):
1. Net size in big trades over 5 min ≥ 40 contracts (big money is buying)
2. Bar delta and rolling delta both positive (the rest of the tape agrees)
3. Stacked buy imbalance or sell absorption on the bar (structure confirms)
4. Price above VWAP but not more than 3 ATR past it (don't chase)

Exits: stop, TP1 (half off, stop to breakeven), TP2, 30-minute time stop, or big flow flipping against the trade.

## Prop-firm rules + profiles

Enter your firm's numbers in dollars (NinjaTrader group "0. Prop firm rules", or
`[propfirm]` in `config.toml`): account size, profit target (0 = funded), max
drawdown as **static / end-of-day trailing / intraday trailing** with optional
lock, daily loss limit, consistency %, contract cap, minimum profitable days.

- Stops a safety buffer **before** every firm limit
- Never opens a trade whose stop-out could breach the drawdown floor or the daily limit
- Size = a % of the room above the drawdown floor, so it shrinks near the limit
- Profit lock, losing-streak cooldown, adaptive risk on top
- Session times in US Eastern; daylight saving is automatic

| Profile | Use for | Risk per trade |
|---|---|---|
| **Evaluation** (`config.toml`) | passing the challenge fast | 20% of the room above the floor |
| **Funded** (`config_funded.toml`) | keeping the account, ~2%/month goal | 8%, tighter daily stop, 3-loss cooldown |

## Fill model — built not to flatter you

- Market entries fill ≥ 50 ms after the signal, +1 tick slippage
- Stops fill at the worse of the stop or the gap price, −1 tick
- Take-profits fill only if price trades **through** the level
- Commission per contract per side

## Python research tools

```
pip install -r requirements.txt
python tests/test_fills.py && python tests/test_risk.py      # fill-model + prop-firm rule checks
python run_backtest.py --data synthetic                      # plumbing test (evaluation rules)
python run_backtest.py --config config_funded.toml --data synthetic
python robustness_sweep.py                                   # 10 runs, count passes / failures
python orderflow_chart.py --data synthetic --date 2026-06-03 # price + big trades + delta chart
```

Real CME data (Databento):
```
export DATABENTO_API_KEY=db-xxxx
python fetch_databento.py --start 2026-09-01 --end 2026-09-06        # shows the cost first
python fetch_databento.py --start 2026-09-01 --end 2026-09-06 --yes
python run_backtest.py --data GC_v_0_trades_2026-09-01_2026-09-06.dbn.zst
python paper_trade.py                                        # live tape, simulated fills
```
Data exported from NinjaTrader: `python run_backtest.py --data orderflow_MGC_....csv`

## Real-data backtest, March to today (MT5-style reports)

```
pip install -r requirements.txt
set DATABENTO_API_KEY=db-xxxxxxxx            # new Databento accounts get free credit
python run_real_backtest.py --start 2026-03-01           # shows the cost per month, downloads nothing
python run_real_backtest.py --start 2026-03-01 --yes     # downloads GC trades month by month and runs
```
Or with data exported from NinjaTrader (`OrderFlowTickExporter`, GC or MGC, 1-tick, Tick Replay on):
```
python run_real_backtest.py --files "orderflow_*.csv" --start 2026-03-01
```
Writes `reports/<date>/`: `funded_continuous.html` (funded profile through the whole period),
`challenge_attempts.html` (a fresh evaluation started on the 1st of each month, up to 45 days each)
and one full report per attempt. Each report has the MT5 tester layout: settings, results,
drawdowns, trade statistics, balance/equity chart, monthly and hourly breakdowns, prop-firm
rule events and the full deals list.

## Read this before trusting any number

Everything in `results/` comes from a **synthetic** tape with a planted edge. It
proves the machinery works — nothing about real gold. $50K account, $2,000
intraday trailing drawdown, 22 trading days, 10 runs per test:

| | Evaluation profile | Funded profile |
|---|---|---|
| Planted edge | 7/10 passed, median 17 trading days | median +3.5% over the run, 8/10 runs ≥ +2% |
| No edge | 0 passed, about −2.5% | about −1.5% |
| Drawdown limit hit | 0 of 20 runs | 0 of 20 runs |

The risk layer keeps the account alive; it cannot make a strategy without an edge profitable.

## Next steps

1. Backtest 3–6 months of real MGC/GC data (Strategy Analyzer, 1-tick, Tick Replay on)
2. Look for profit factor > ~1.3 after costs, 100+ trades, profit spread across months
3. Tune on half the data, check on the other half
4. Paper trade 2–4 weeks on Sim101 before a paid evaluation
5. Confirm in writing that your firm allows automated trading on the **funded** account and supports NinjaTrader
