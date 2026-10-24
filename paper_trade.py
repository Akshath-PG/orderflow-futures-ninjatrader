"""Paper-trade on the LIVE CME tape with the exact same code as the backtest.

  export DATABENTO_API_KEY=db-xxxxxxxx
  python paper_trade.py

No real orders are sent. Fills are simulated with the backtest fill model,
so live paper results are directly comparable to backtest results.
Going live = replacing the simulated fills with a broker adapter
(Rithmic / Tradovate / Interactive Brokers) behind the same calls.
"""
import tomllib
from datetime import datetime, timezone

from orderflow import Backtester
from orderflow.data_sources import databento_live


def main():
    with open("config.toml", "rb") as fh:
        cfg = tomllib.load(fh)
    bot = Backtester(cfg)
    bot.on_fill = lambda msg: print(datetime.now(timezone.utc).strftime("%H:%M:%S"), msg,
                                    f"| balance {bot.balance:.2f}", flush=True)
    print(f"Paper trading {cfg['instrument']['exec_symbol']} on flow from "
          f"{cfg['instrument']['data_symbol']} - Ctrl+C to stop")
    try:
        for t in databento_live(cfg["instrument"]["data_symbol"]):
            bot.on_tick(t)
    except KeyboardInterrupt:
        print(f"\nStopped. Trades: {len(bot.trades)}  Balance: {bot.balance:.2f}")


if __name__ == "__main__":
    main()
