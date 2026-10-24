# NinjaTrader 8 Setup — OrderFlowFollower

Two files:

| File | What it is |
|---|---|
| `OrderFlowFollower.cs` | The trading strategy (same logic as the Python bot) |
| `OrderFlowTickExporter.cs` | Optional. Places no trades — dumps NinjaTrader's tick data to CSV for the Python tools |

## Data: no separate API needed

The strategy reads every trade (and the bid/ask at that instant) straight from
NinjaTrader's own market-data connection through `OnMarketData`. Whatever
connection you log into supplies the data:

- **NinjaTrader's free simulation account / trial** — live data plus free historical tick data for replay and backtesting.
- **Your prop firm's login** — futures prop firms usually hand you a Rithmic or Tradovate connection for NinjaTrader. Use that.

You need a connection that provides **historical tick data with bid/ask**.
If it doesn't, the strategy notices (it sees almost no buyers) and prints a
warning in the Output window, then falls back to a weaker method.

## 1. Install

1. Copy both `.cs` files into
   `Documents\NinjaTrader 8\bin\Custom\Strategies\`
2. In NinjaTrader: **New > NinjaScript Editor**, open `OrderFlowFollower`, press **F5** to compile.
3. If the bottom panel shows any errors, copy them and send them to me — I wrote
   this without access to NinjaTrader's compiler, so a small fix may be needed.

## 2. Turn on Tick Replay (required)

**Tools > Options > Market Data > tick "Show Tick Replay"**. Without this,
order-flow logic gets no historical trades and the strategy warns you.

## 3. Backtest in the Strategy Analyzer

**New > Strategy Analyzer**, then:

| Setting | Value |
|---|---|
| Strategy | OrderFlowFollower |
| Instrument | Micro gold front month, e.g. `MGC 12-26` |
| Data series | Type **Tick**, Value **1** |
| Tick Replay | **On** |
| Date range | Start with 1–2 weeks (tick replay is slow) |
| Commission | Pick a commission template so the report includes costs |

Using a 1-tick series means orders fill on the next real trade, which keeps
fills realistic. (NinjaTrader can't combine Tick Replay with "High" fill
resolution, so the 1-tick series is how we get fine-grained fills.)

Results: the Analyzer report, plus a line per signal/trade/risk event in
**New > NinjaScript Output** (lines start with `[OFF]`).

**Read flow from GC, trade MGC (optional):** set *Flow instrument* to the
full-size contract, e.g. `GC 12-26`. Large traders mostly trade GC, so its
tape is the stronger signal. NinjaTrader warns that multi-instrument Tick
Replay can drift out of sync, so validate this against the single-instrument
setup before trusting it.

## 4. Paper trade before real money

Put the strategy on a 1-tick MGC chart with the **Sim101** account and let it
run for at least 2 weeks. Compare its results to the backtest. Only then
switch the account to your prop-firm account.

## Pick a profile first

The first setting, **Profile**, sets how hard the bot pushes. It changes only
the bot's own risk settings — the firm rules you enter are never touched.

| | **Evaluation** | **Funded** |
|---|---|---|
| Goal | pass the evaluation fast | keep the account, ~2% a month |
| Risk per trade | 20% of the room above the floor (~$360 on a $50K / $2K account) | 8% of the room (~$145) |
| Max contracts | 15 (firm cap still applies) | 8 |
| Daily stop | firm daily limit minus buffer | 20% of the drawdown ($400 on $2K) |
| Losing streak | stop 24h after 4 losses | stop 48h after 3 losses |
| Profit lock | arms at 50% of the drawdown in profit ($1,000 on $2K), gives back up to 50% | arms at 20% ($400 on $2K), gives back up to 35% |
| Profit target setting | your firm's target | **0** |

Why the difference: failing an evaluation costs a reset fee; failing a funded
account loses the account. **Custom** uses the "5. Bot risk" values as typed.

## Settings — enter your prop firm's rules

Group **0. Prop firm rules** is where your account's rules go. Copy the numbers
from your firm's rules page for your exact account size and type.

| Setting | What to enter |
|---|---|
| Account size ($) | e.g. 50000 |
| Profit target ($) | evaluation target, e.g. 3000. **0 for a funded account** (no target, bot keeps trading) |
| Max drawdown ($) | the firm's max loss / trailing drawdown, e.g. 2000 |
| Drawdown type | **Static** (floor never moves), **EndOfDayTrailing** (floor rises with each day's closing balance), **IntradayTrailing** (floor rises with your equity peak in real time, including open profit) |
| Trailing stops at start balance | on if the firm's trailing floor stops rising once it reaches your starting balance |
| Trail lock offset ($) | where it stops relative to the start, e.g. 100 if it locks at start + $100 |
| Daily loss limit ($) | firm's daily limit, 0 if none |
| Firm max contracts | the firm's cap **in the contract you trade** — micros usually count 10 per mini, so 5 minis = 50 MGC |
| Consistency % | e.g. 50 if your best day must stay under 50% of the target. The bot stops for the day at 90% of that |
| Daily profit cap ($) | for funded accounts with a consistency rule on payouts, the most you want to make in one day; 0 = off |
| Safety buffer ($) | how far **before** any firm limit the bot stops (default 200), to absorb slippage |
| Bot daily stop ($) | your own tighter daily stop; 0 = firm limit minus buffer, or half the drawdown if the firm has none |

What the bot does with them:
- **Drawdown guard:** stops permanently when equity gets within the safety buffer of the firm's floor. The floor is tracked exactly as the drawdown type says.
- **Daily stop:** closes everything and stops for the day at the daily allowance; resumes next trading day (18:00 ET).
- **Pre-trade check:** never opens a trade whose full stop-out could breach the floor or the daily allowance — it trades fewer contracts or skips.
- **Position size** = *Risk % of room* (default 12%) of the dollars between equity and the floor. As the account nears its limit, positions shrink automatically.
- **Profit target / consistency:** stops once equity is a quarter of the safety buffer past the target (so closing slippage can't leave you short); stops for the day before a single day breaks the consistency rule.

Every rule event is printed in **NinjaScript Output** with a `[OFF]` prefix,
and the first line shows the rules as the bot understood them — check it.

**Session times are US Eastern (HHMM)** and daylight saving is handled for
you. Defaults: trading day starts 1800, first entry 0830, last entry 1500,
flatten 1600. Set *Flatten all* earlier than your firm's cutoff.

### Before you pick a firm — check two things in writing
1. **Automated trading allowed on the funded account**, not just the evaluation.
   Several firms allow bots in evaluation but ban full automation once funded.
2. **NinjaTrader is a supported platform** for that firm (usually via a Rithmic or Tradovate login).

The bot follows the numbers you enter; it can't check them against the firm.
Firms change rules often, so re-check the rules page whenever you buy a new account.

## 5. Export NinjaTrader data to the Python tools (optional)

Run `OrderFlowTickExporter` in the Strategy Analyzer with the same 1-tick,
Tick Replay setup. It writes
`Documents\NinjaTrader 8\orderflow_<instrument>_<time>.csv`. Then:

```
python run_backtest.py --data "orderflow_MGC_12-26_20261010_101500.csv"
python robustness_sweep.py      # synthetic only; run the backtest on several exported months for real data
```
