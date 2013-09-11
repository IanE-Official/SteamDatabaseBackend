﻿/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class Steam
    {
        public const uint TEAM_FORTRESS_2 = 440;

        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamApps steamApps;
        public SteamFriends steamFriends;
        public SteamUserStats steamUserStats;
        private SteamGameCoordinator gameCoordinator;

        public CallbackManager manager;

        private uint PreviousChange;

        private bool fullRun;
        public bool isRunning = true;

        public System.Timers.Timer timer;

        public void GetPICSChanges()
        {
            steamApps.PICSGetChangesSince(PreviousChange, true, true);
        }

        private void GetLastChangeNumber()
        {
            // If we're in a full run, request all changes from #1
            if (!fullRun && Settings.Current.FullRun > 0)
            {
                PreviousChange = 1;

                return;
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1"))
            {
                if (Reader.Read())
                {
                    PreviousChange = Reader.GetUInt32("ChangeID");

                    Log.WriteInfo("Steam", "Previous changelist was {0}", PreviousChange);
                }
            }

            if (PreviousChange == 0)
            {
                Log.WriteWarn("Steam", "Looks like there are no changelists in the database.");
                Log.WriteWarn("Steam", "If you want to fill up your database first, restart with \"FullRun\" setting set to 1.");
            }
        }

        public void Run()
        {
            steamClient = new SteamClient();
            steamUser = steamClient.GetHandler<SteamUser>();
            steamApps = steamClient.GetHandler<SteamApps>();
            steamFriends = steamClient.GetHandler<SteamFriends>();
            steamUserStats = steamClient.GetHandler<SteamUserStats>();
            gameCoordinator = steamClient.GetHandler<SteamGameCoordinator>();

            manager = new CallbackManager(steamClient);

            timer = new System.Timers.Timer();
            timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimer);
            timer.Interval = TimeSpan.FromSeconds(1).TotalMilliseconds;

            new Callback<SteamClient.ConnectedCallback>(OnConnected, manager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, manager);

            new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo, manager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, manager);
            new Callback<SteamUser.LoggedOffCallback>(OnLoggedOff, manager);

            new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage, manager);

            new JobCallback<SteamApps.PICSChangesCallback>(OnPICSChanges, manager);
            new JobCallback<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo, manager);

            // irc specific
            new Callback<SteamFriends.ClanStateCallback>(Program.ircSteam.OnClanState, Program.steam.manager);
            new JobCallback<SteamUserStats.NumberOfPlayersCallback>(Program.ircSteam.OnNumberOfPlayers, Program.steam.manager);

            GetLastChangeNumber();

            steamClient.Connect();

            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        private void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            GetPICSChanges();
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                IRC.SendEmoteAnnounce("failed to connect: {0}", callback.Result);

                throw new Exception("Could not connect: " + callback.Result);
            }

            Log.WriteInfo("Steam", "Connected! Logging in...");

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.Steam.Username,
                Password = Settings.Current.Steam.Password
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            timer.Stop();

            if (!isRunning)
            {
                Log.WriteInfo("Steam", "Disconnected from Steam");
                return;
            }

            const uint RETRY_DELAY = 15;

            Log.WriteInfo("Steam", "Disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

            IRC.SendEmoteAnnounce("disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

            Thread.Sleep(TimeSpan.FromSeconds(RETRY_DELAY));

            steamClient.Connect();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Log.WriteError("Steam", "Failed to login: {0}", callback.Result);

                IRC.SendEmoteAnnounce("failed to log in: {0}", callback.Result);

                Thread.Sleep(TimeSpan.FromSeconds(2));

                return;
            }

            string serverTime = callback.ServerTime.ToString();

            Log.WriteInfo("Steam", "Logged in, current valve time is {0} UTC", serverTime);

            IRC.SendEmoteAnnounce("is now logged in. Server time: {0} UTC", serverTime);

            // Prevent bugs
            if (fullRun)
            {
                return;
            }

            if (Settings.Current.FullRun > 0)
            {
                fullRun = true;

                GetPICSChanges();
            }
            else
            {
                timer.Start();

                SteamProxy.PlayGame(steamClient, TEAM_FORTRESS_2);
            }

#if DEBUG
            steamApps.PICSGetProductInfo(440, 29197, false, false);
#endif
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log.WriteInfo("Steam", "Logged off of Steam");

            IRC.SendEmoteAnnounce("logged off of Steam.");
        }

        private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            steamFriends.SetPersonaState(EPersonaState.Busy);
        }

        private void OnGameCoordinatorMessage(SteamGameCoordinator.MessageCallback callback)
        {
            SteamProxy.GameCoordinatorMessage(TEAM_FORTRESS_2, callback, gameCoordinator);
        }

        private void OnPICSChanges(SteamApps.PICSChangesCallback callback, JobID job)
        {
            if (fullRun)
            {
                // Hackiness to prevent processing legit changelists after our request
                if (PreviousChange == 1)
                {
                    PreviousChange = 2;

                    Log.WriteInfo("Steam", "Requesting info for {0} apps and {1} packages", callback.AppChanges.Count, callback.PackageChanges.Count);

                    steamApps.PICSGetProductInfo(callback.AppChanges.Keys, callback.PackageChanges.Keys, false, false);
                }
                else
                {
                    Log.WriteWarn("Steam", "Got changelist {0}, but ignoring it because we're in a full run", callback.CurrentChangeNumber);
                }

                return;
            }
            else if (PreviousChange == callback.CurrentChangeNumber)
            {
                return;
            }

            PreviousChange = callback.CurrentChangeNumber;

            Log.WriteInfo("Steam", "Got changelist {0}, previous is {1} ({2} apps, {3} packages)", callback.CurrentChangeNumber, PreviousChange, callback.AppChanges.Count, callback.PackageChanges.Count);

            Task.Factory.StartNew(delegate
            {
                Program.ircSteam.OnPICSChanges(callback.CurrentChangeNumber, callback);
            });

            DbWorker.ExecuteNonQuery("INSERT INTO Changelists (ChangeID) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE Date = CURRENT_TIMESTAMP()", new MySqlParameter("@ChangeID", callback.CurrentChangeNumber));

            if (callback.AppChanges.Count == 0 && callback.PackageChanges.Count == 0)
            {
                return;
            }

            Task.Factory.StartNew(delegate
            {
                foreach (var callbackapp in callback.AppChanges)
                {
                    if (callback.CurrentChangeNumber != callbackapp.Value.ChangeNumber)
                    {
                        DbWorker.ExecuteNonQuery("INSERT INTO Changelists (ChangeID) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE Date = Date", new MySqlParameter("@ChangeID", callbackapp.Value.ChangeNumber));
                    }

                    DbWorker.ExecuteNonQuery("UPDATE Apps SET LastUpdated = CURRENT_TIMESTAMP() WHERE AppID = @AppID", new MySqlParameter("@AppID", callbackapp.Value.ID));

                    DbWorker.ExecuteNonQuery("INSERT IGNORE INTO ChangelistsApps (ChangeID, AppID) VALUES (@ChangeID, @AppID)",
                                             new MySqlParameter("@ChangeID", callbackapp.Value.ChangeNumber),
                                             new MySqlParameter("@AppID", callbackapp.Value.ID)
                    );
                }

                foreach (var callbackpack in callback.PackageChanges)
                {
                    if (callback.CurrentChangeNumber != callbackpack.Value.ChangeNumber)
                    {
                        DbWorker.ExecuteNonQuery("INSERT INTO Changelists (ChangeID) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE Date = Date", new MySqlParameter("@ChangeID", callbackpack.Value.ChangeNumber));
                    }

                    DbWorker.ExecuteNonQuery("UPDATE Subs SET LastUpdated = CURRENT_TIMESTAMP WHERE SubID = @SubID", new MySqlParameter("@SubID", callbackpack.Value.ID));

                    DbWorker.ExecuteNonQuery("INSERT IGNORE INTO ChangelistsSubs (ChangeID, SubID) VALUES (@ChangeID, @SubID)",
                                             new MySqlParameter("@ChangeID", callbackpack.Value.ChangeNumber),
                                             new MySqlParameter("@SubID", callbackpack.Value.ID)
                    );
                }
            });

            steamApps.PICSGetProductInfo(callback.AppChanges.Keys, callback.PackageChanges.Keys, false, false);
        }

        private void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback, JobID jobID)
        {
            var request = Program.ircSteam.IRCRequests.Find(r => r.JobID == jobID);

            if (request != null)
            {
                Program.ircSteam.IRCRequests.Remove(request);

                Task.Factory.StartNew(delegate
                {
                    Program.ircSteam.OnProductInfo(request, callback);
                });

                return;
            }

            foreach (var app in callback.Apps)
            {
                Log.WriteDebug("Steam", "AppID: {0}", app.Key);

                var workaround = app;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    new AppProcessor(workaround.Key).Process(workaround.Value);
                });
            }

            foreach (var package in callback.Packages)
            {
                Log.WriteDebug("Steam", "SubID: {0}", package.Key);

                var workaround = package;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    new SubProcessor(workaround.Key).Process(workaround.Value);
                });
            }

            // Only handle when fullrun is disabled or if it specifically is running with mode "2" (full run inc. unknown apps)
            if (Settings.Current.FullRun != 1)
            {
                foreach (uint app in callback.UnknownApps)
                {
                    uint workaround = app;

                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        new AppProcessor(workaround).ProcessUnknown();
                    });
                }

                foreach (uint package in callback.UnknownPackages)
                {
                    Log.WriteWarn("Steam", "Unknown SubID: {0} - We don't handle these yet", package);
                }
            }
        }
    }
}