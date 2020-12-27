﻿using System;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Chaincase;
using Chaincase.Common;
using Chaincase.Common.Contracts;
using Chaincase.Common.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using ReactiveUI;
using Splat;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.CoinJoin.Client.Clients.Queuing;
using WalletWasabi.CoinJoin.Client.Rounds;
using WalletWasabi.CoinJoin.Common.Models;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;

namespace Chaincase.UI.ViewModels
{
	public class CoinJoinViewModel : ReactiveObject
	{
		protected Global Global { get; }
        private CompositeDisposable Disposables { get; set; }

        private INotificationManager notificationManager;

        private string _coordinatorFeePercent;
		private int _peersRegistered;
		private int _peersNeeded;

		private RoundPhaseState _roundPhaseState;
		private DateTimeOffset _roundTimesout;
		private TimeSpan _timeLeftTillRoundTimeout;
		private Money _requiredBTC;
		private Money _amountQueued;
		private bool _isDequeueBusy;
		private bool _isEnqueueBusy;
        private bool _isQueuedToCoinJoin = false;
		private string _balance;
        private SelectCoinsViewModel _coinList;
		public CoinJoinViewModel(Global global) 
        {
            Global = global;
            if (Disposables != null)
            {
                throw new Exception("Wallet opened before it was closed.");
            }

            Disposables = new CompositeDisposable();
			// Sum UTXOs
			//SetBalance();
			// Start with 0 btc until user chooses some UTXOs
			AmountQueued = Money.Zero;
            // Infer coordinator fee
            var registrableRound = Global.Wallet.ChaumianClient.State.GetRegistrableRoundOrDefault();
            CoordinatorFeePercent = registrableRound?.State?.CoordinatorFeePercent.ToString() ?? "0.003";

            // Select most advanced coin join round
            ClientRound mostAdvancedRound = Global.Wallet.ChaumianClient?.State?.GetMostAdvancedRoundOrDefault();
            if (mostAdvancedRound != default)
            {
                RoundPhaseState = new RoundPhaseState(mostAdvancedRound.State.Phase, Global.Wallet.ChaumianClient?.State.IsInErrorState ?? false);
                RoundTimesout = mostAdvancedRound.State.Phase == RoundPhase.InputRegistration ? mostAdvancedRound.State.InputRegistrationTimesout : DateTimeOffset.UtcNow;
                PeersRegistered = mostAdvancedRound.State.RegisteredPeerCount;
                PeersNeeded = mostAdvancedRound.State.RequiredPeerCount;
            }
            else
            {
                RoundPhaseState = new RoundPhaseState(RoundPhase.InputRegistration, false);
                RoundTimesout = DateTimeOffset.UtcNow;
                PeersRegistered = 0;
                PeersNeeded = 100;
            }

            // Set time left in round 
            this.WhenAnyValue(x => x.RoundTimesout)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    TimeSpan left = RoundTimesout - DateTimeOffset.UtcNow;
                    TimeLeftTillRoundTimeout = left > TimeSpan.Zero ? left : TimeSpan.Zero; // Make sure cannot be less than zero.
                });
            // TODO
            //Observable
            //    .FromEventPattern<SmartCoin>(CoinList, nameof(CoinList.DequeueCoinsPressed))
            //    .Subscribe(async x => await DoDequeueAsync(x.EventArgs));


            // Update view model state on chaumian client state updates
            Observable.FromEventPattern(Global.Wallet.ChaumianClient, nameof(Global.Wallet.ChaumianClient.CoinQueued))
                .Merge(Observable.FromEventPattern(Global.Wallet.ChaumianClient, nameof(Global.Wallet.ChaumianClient.OnDequeue)))
                .Merge(Observable.FromEventPattern(Global.Wallet.ChaumianClient, nameof(Global.Wallet.ChaumianClient.StateUpdated)))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateStates())
                .DisposeWith(Disposables);

            // Remove notification on unconfirming status in coin join round
            Observable.FromEventPattern(Global.Wallet.ChaumianClient, nameof(Global.Wallet.ChaumianClient.OnDequeue))
                   .Subscribe(pattern =>
                   {
                       var e = (DequeueResult)pattern.EventArgs;
                       try
                       {
                           foreach (var success in e.Successful.Where(x => x.Value.Any()))
                           {
                               DequeueReason reason = success.Key;
                               if (reason == DequeueReason.UserRequested)
                               {
                                   notificationManager.RemoveAllPendingNotifications();
                               }
                           }
                       }
                       catch (Exception ex)
                       {
                           Logger.LogWarning(ex);
                       }
                   })
                   .DisposeWith(Disposables);
            // Update timeout label
            Observable.Interval(TimeSpan.FromSeconds(1))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    TimeSpan left = RoundTimesout - DateTimeOffset.UtcNow;
                    TimeLeftTillRoundTimeout = left > TimeSpan.Zero ? left : TimeSpan.Zero; // Make sure cannot be less than zero.
                }).DisposeWith(Disposables);
        }

        private void UpdateStates()
        {
            var chaumianClient = Global.Wallet.ChaumianClient;
            if (chaumianClient is null)
            {
                return;
            }

            AmountQueued = chaumianClient.State.SumAllQueuedCoinAmounts();

            var registrableRound = chaumianClient.State.GetRegistrableRoundOrDefault();
            if (registrableRound != default)
            {
                CoordinatorFeePercent = registrableRound.State.CoordinatorFeePercent.ToString();
                UpdateRequiredBtcLabel(registrableRound);
            }

            var mostAdvancedRound = chaumianClient.State.GetMostAdvancedRoundOrDefault();
            if (mostAdvancedRound != default)
            {
                if (!chaumianClient.State.IsInErrorState)
                {
                    RoundPhaseState = new RoundPhaseState(mostAdvancedRound.State.Phase, false);
                    RoundTimesout = mostAdvancedRound.State.Phase == RoundPhase.InputRegistration ? mostAdvancedRound.State.InputRegistrationTimesout : DateTimeOffset.UtcNow;
                }
                else
                {
                    RoundPhaseState = new RoundPhaseState(RoundPhaseState.Phase, true);
                }
                this.RaisePropertyChanged(nameof(RoundPhaseState));
                this.RaisePropertyChanged(nameof(RoundTimesout));
                PeersRegistered = mostAdvancedRound.State.RegisteredPeerCount;
                PeersNeeded = mostAdvancedRound.State.RequiredPeerCount;
            }
        }

        private void UpdateRequiredBtcLabel(ClientRound registrableRound)
        {
            if (Global.WalletManager is null)
            {
                return; // Otherwise NullReferenceException at shutdown.
            }

            if (registrableRound == default)
            {
                if (RequiredBTC == default)
                {
                    RequiredBTC = Money.Zero;
                }
            }
            else
            {
                var coins = Global.Wallet.Coins;
                var queued = coins.CoinJoinInProcess();
                if (queued.Any())
                {
                    RequiredBTC = registrableRound.State.CalculateRequiredAmount(Global.Wallet.ChaumianClient.State.GetAllQueuedCoinAmounts().ToArray());
                }
                else
                {
                    var available = coins.Confirmed().Available();
                    RequiredBTC = available.Any()
                        ? registrableRound.State.CalculateRequiredAmount(available.Where(x => x.AnonymitySet < Global.Config.PrivacyLevelStrong).Select(x => x.Amount).ToArray())
                        : registrableRound.State.CalculateRequiredAmount();
                }
            }
        }

        private void SetBalance()
        {
            try
            {
				Balance = (
					Enumerable.Where(Global.Wallet.Coins,
						c => c.Unspent && !c.SpentAccordingToBackend
					).Sum(c => (long?)c.Amount) ?? 0
					).ToString();
			}
            catch (Exception error) {
                Logger.LogError($"CoinJoinViewModel.SetBalance(): Failed to retrieve balance {error} ");
                Balance = "0";
            }
        }

		public SelectCoinsViewModel CoinList
		{
			get => _coinList;
			set => this.RaiseAndSetIfChanged(ref _coinList, value);
		}

		public Money AmountQueued
        {
            get => _amountQueued;
            set => this.RaiseAndSetIfChanged(ref _amountQueued, value);
        }

        public Money RequiredBTC
        {
            get => _requiredBTC;
            set => this.RaiseAndSetIfChanged(ref _requiredBTC, value);
        }

        public string CoordinatorFeePercent
        {
            get => _coordinatorFeePercent;
            set => this.RaiseAndSetIfChanged(ref _coordinatorFeePercent, value);
        }

        public string Balance
        {
            get => _balance;
            set => this.RaiseAndSetIfChanged(ref _balance, value);
        }

        public int PeersNeeded
        {
            get => _peersNeeded;
            set => this.RaiseAndSetIfChanged(ref _peersNeeded, value);
        }

        public int PeersRegistered
        {
            get => _peersRegistered;
            set => this.RaiseAndSetIfChanged(ref _peersRegistered, value);
        }

        public RoundPhaseState RoundPhaseState
        {
            get => _roundPhaseState;
            set => this.RaiseAndSetIfChanged(ref _roundPhaseState, value);
        }

        public DateTimeOffset RoundTimesout
        {
            get => _roundTimesout;
            set => this.RaiseAndSetIfChanged(ref _roundTimesout, value);
        }

        public TimeSpan TimeLeftTillRoundTimeout
        {
            get => _timeLeftTillRoundTimeout;
            set => this.RaiseAndSetIfChanged(ref _timeLeftTillRoundTimeout, value);
        }

        public bool IsEnqueueBusy
        {
            get => _isEnqueueBusy;
            set => this.RaiseAndSetIfChanged(ref _isEnqueueBusy, value);
        }

        public bool IsDequeueBusy
        {
            get => _isDequeueBusy;
            set => this.RaiseAndSetIfChanged(ref _isDequeueBusy, value);
        }

        public bool IsQueuedToCoinJoin
        {
            get => _isQueuedToCoinJoin;
            set => this.RaiseAndSetIfChanged(ref _isDequeueBusy, value);
        }

        public bool IsSynced
        {
            get => true;
            set => this.RaiseAndSetIfChanged(ref _isDequeueBusy, value);
        }

		public decimal QueuedPercentage => Math.Round((decimal) (PeersRegistered / PeersNeeded) * 100, 2, MidpointRounding.AwayFromZero);
	}
}
