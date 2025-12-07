#region Using declarations
using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class ATRVWAP_RSI_MultiContract : Strategy
    {
        // --- USER PARAMETERS ---
        private int numContracts = 3;                // Total contracts
        private double[] targetATRMultipliers;       // ATR multipliers per contract
        private double stopATRMultiplier = 2.5;      // Hard stop = 2.5x ATR
        private double breakEvenTriggerATR = 3.0;    // Move stop to BE at 3x ATR
        private double dailyLossPct = 1.0;           // 1% daily max loss
        private int rsiLength = 14;
        private int emaFast = 50;
        private int emaSlow = 200;
        private double stdDevMultiplier = 1.0;

        private EMA emaFastDaily;
        private EMA emaSlowDaily;
        private RSI rsi;
        private ATR atr;
        private VWAP weeklyVWAP;
        private StdDev weeklyStd;

        private double dailyStartBalance;
        private double dailyMaxLoss;
        private double entryPrice;

        // Trading window (6:30am – 10:30am PT = 9:30am – 1:30pm ET)
        private TimeSpan sessionStart = new TimeSpan(9, 30, 0);
        private TimeSpan sessionEnd = new TimeSpan(13, 30, 0);

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ATR VWAP RSI Multi-Contract Strategy";
                Calculate = Calculate.OnBarClose;

                // Default ATR profit targets per contract
                targetATRMultipliers = new double[] { 3.0, 6.0, 9.0 };
            }

            if (State == State.Configure)
            {
                // Weekly VWAP + StdDev via AddDataSeries
                AddDataSeries(BarsPeriodType.Week, 1);
            }

            if (State == State.DataLoaded)
            {
                emaFastDaily = EMA(emaFast);
                emaSlowDaily = EMA(emaSlow);

                rsi = RSI(rsiLength, 1);
                atr = ATR(14);

                weeklyVWAP = VWAP(1);
                weeklyStd = StdDev(weeklyVWAP, 20);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return; // Only trade main series

            // --- TIME FILTER ---
            if (ToTime(Time[0]) < ToTime(sessionStart) ||
                ToTime(Time[0]) > ToTime(sessionEnd))
                return;

            // --- DAILY LOSS LIMIT ---
            if (Bars.IsFirstBarOfSession)
            {
                dailyStartBalance = Account.Get(AccountItem.CashValue, Currency.UsDollar);
                dailyMaxLoss = dailyStartBalance * (dailyLossPct / 100.0);
            }

            double currentBalance = Account.Get(AccountItem.CashValue, Currency.UsDollar);
            if (dailyStartBalance - currentBalance >= dailyMaxLoss)
                return; // Stop trading for the day

            // --- DAILY TREND FILTER ---
            if (CurrentBar < 200) return;
            bool longBias = emaFastDaily[0] > emaSlowDaily[0];
            bool shortBias = emaFastDaily[0] < emaSlowDaily[0];

            // --- WEEKLY VWAP FUNNEL FILTER ---
            double vwap = weeklyVWAP[0];
            double plus1 = vwap + weeklyStd[0] * stdDevMultiplier;
            double minus1 = vwap - weeklyStd[0] * stdDevMultiplier;

            bool allowLong = longBias && GetCurrentBid() > vwap && GetCurrentBid() < plus1;
            bool allowShort = shortBias && GetCurrentAsk() < vwap && GetCurrentAsk() > minus1;

            // --- ENTRY LOGIC ---
            double atrVal = atr[0];

            if (allowLong && CrossAbove(rsi, 20, 1))
            {
                entryPrice = Close[0];
                EnterLong(numContracts);
            }

            if (allowShort && CrossBelow(rsi, 80, 1))
            {
                entryPrice = Close[0];
                EnterShort(numContracts);
            }

            // --- MANAGE OPEN POSITIONS ---
            if (Position.MarketPosition == MarketPosition.Long)
            {
                for (int i = 0; i < numContracts; i++)
                {
                    double profitTarget = entryPrice + targetATRMultipliers[i] * atrVal;
                    double stopLoss = entryPrice - stopATRMultiplier * atrVal;

                    SetProfitTarget(CalculationMode.Price, profitTarget);
                    SetStopLoss(CalculationMode.Price, stopLoss);
                }

                // Break-even logic
                if (Close[0] >= entryPrice + breakEvenTriggerATR * atrVal)
                    SetStopLoss(CalculationMode.Price, entryPrice + (2 * TickSize));
            }

            if (Position.MarketPosition == MarketPosition.Short)
            {
                for (int i = 0; i < numContracts; i++)
                {
                    double profitTarget = entryPrice - targetATRMultipliers[i] * atrVal;
                    double stopLoss = entryPrice + stopATRMultiplier * atrVal;

                    SetProfitTarget(CalculationMode.Price, profitTarget);
                    SetStopLoss(CalculationMode.Price, stopLoss);
                }

                if (Close[0] <= entryPrice - breakEvenTriggerATR * atrVal)
                    SetStopLoss(CalculationMode.Price, entryPrice - (2 * TickSize));
            }
        }
    }
}
