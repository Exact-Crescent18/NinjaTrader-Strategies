#region Using declarations
using System;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class VWAPFadeMR_MultiScale : Strategy
    {
        // User Inputs
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name="Number of Contracts", Order=1, GroupName="Parameters")]
        public int NumContracts { get; set; } = 3;

        [NinjaScriptProperty]
        [Display(Name="ATR Stop Multiplier", Order=2, GroupName="Parameters")]
        public double ATRStopMult { get; set; } = 2.5;

        [NinjaScriptProperty]
        [Display(Name="ATR Target Multiples (comma-separated)", Order=3, GroupName="Parameters")]
        public string ATRTargetMultiplesCSV { get; set; } = "3,6,9";

        [NinjaScriptProperty]
        [Range(0.1, 5)]
        [Display(Name="VWAP Z-Score Threshold", Order=4, GroupName="Parameters")]
        public double ZScoreThreshold { get; set; } = 1.0;

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name="RSI Period", Order=5, GroupName="Parameters")]
        public int RSIPeriod { get; set; } = 5;

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="RSI Oversold", Order=6, GroupName="Parameters")]
        public double RSIOversold { get; set; } = 30;

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name="RSI Overbought", Order=7, GroupName="Parameters")]
        public double RSIOverbought { get; set; } = 70;

        [NinjaScriptProperty]
        [Display(Name="Daily Max Loss ($)", Order=8, GroupName="Risk")]
        public double DailyMaxLoss { get; set; } = 50;

        // Internal vars
        private double[] ATRTargetMultiplesArray;
        private double dailyLoss = 0;
        private bool sessionActive = false;

        private ATR atr;
        private RSI rsi;
        private EMA ema50Daily;
        private EMA ema200Daily;
        private VWAP vwap;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "VWAPFadeMR_MultiScale";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 0;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(14);
                rsi = RSI(RSIPeriod, 3);
                ema50Daily = EMA(50);
                ema200Daily = EMA(200);
                vwap = VWAP();

                // Parse scale-out targets
                ATRTargetMultiplesArray = ATRTargetMultiplesCSV.Split(',')
                    .Select(s => Convert.ToDouble(s.Trim())).ToArray();

                if (ATRTargetMultiplesArray.Length != NumContracts)
                    throw new Exception("NumContracts must match length of ATRTargetMultiplesCSV");
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 50) return;

            // Session filter 06:30-10:30 PT
            sessionActive = ToTime(Time[0]) >= 63000 && ToTime(Time[0]) <= 103000;
            if (!sessionActive) return;

            // Daily EMA bias
            bool biasLong = ema50Daily[0] > ema200Daily[0];
            bool biasShort = ema50Daily[0] < ema200Daily[0];

            // VWAP Z-score
            double price = Close[0];
            double vwapVal = vwap[0];
            double stddev = StdDev(Close, 20)[0];
            double zScore = (price - vwapVal) / (stddev == 0 ? 0.0001 : stddev);

            // RSI
            double rsiVal = rsi[0];

            // Daily max loss
            if (dailyLoss >= DailyMaxLoss) return;

            // Entry logic
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (biasLong && price < vwapVal && zScore <= -ZScoreThreshold && rsiVal < RSIOversold)
                    EnterLong(NumContracts, "LongFade");

                if (biasShort && price > vwapVal && zScore >= ZScoreThreshold && rsiVal > RSIOverbought)
                    EnterShort(NumContracts, "ShortFade");
            }

            // Manage open positions
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManagePositions();
            }
        }

        private void ManagePositions()
        {
            for (int i = 0; i < Position.Quantity; i++)
            {
                double stopPrice, targetPrice;

                if (Position.MarketPosition == MarketPosition.Long)
                {
                    stopPrice = Position.AveragePrice - ATRStopMult * atr[0];
                    targetPrice = Position.AveragePrice + ATRTargetMultiplesArray[i] * atr[0];

                    ExitLongStopMarket(0, true, 1, stopPrice, $"LongStop_{i}", "LongFade");
                    ExitLongLimit(0, true, 1, targetPrice, $"LongTarget_{i}", "LongFade");

                    if (Close[0] - Position.AveragePrice >= 3 * atr[0])
                    {
                        double breakeven = Position.AveragePrice + 2 * TickSize;
                        ExitLongStopMarket(0, true, 1, breakeven, $"LongBE_{i}", "LongFade");
                    }
                }

                if (Position.MarketPosition == MarketPosition.Short)
                {
                    stopPrice = Position.AveragePrice + ATRStopMult * atr[0];
                    targetPrice = Position.AveragePrice - ATRTargetMultiplesArray[i] * atr[0];

                    ExitShortStopMarket(0, true, 1, stopPrice, $"ShortStop_{i}", "ShortFade");
                    ExitShortLimit(0, true, 1, targetPrice, $"ShortTarget_{i}", "ShortFade");

                    if (Position.AveragePrice - Close[0] >= 3 * atr[0])
                    {
                        double breakeven = Position.AveragePrice - 2 * TickSize;
                        ExitShortStopMarket(0, true, 1, breakeven, $"ShortBE_{i}", "ShortFade");
                    }
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order != null && execution.Order.OrderState == OrderState.Filled)
            {
                if (execution.Order.Name.Contains("Stop"))
                    dailyLoss += quantity * atr[0] * ATRStopMult * TickSize;
            }
        }

        protected override void OnBarClose()
        {
            // Reset daily loss at session start
            if (Bars.IsFirstBarOfSession)
                dailyLoss = 0;
        }
    }
}
