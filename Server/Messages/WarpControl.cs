﻿using System;
using System.IO;
using System.Collections.Generic;
using DarkMultiPlayerCommon;
using MessageStream2;

namespace DarkMultiPlayerServer.Messages
{
    public class WarpControl
    {
        //SUBSPACE
        private static int freeID;
        private static Dictionary<int, Subspace> subspaces = new Dictionary<int, Subspace>();
        private static Dictionary<string, int> offlinePlayerSubspaces = new Dictionary<string, int>();
        private static object createLock = new object();

        //MCW (Uses subspace internally)
        private static string warpMaster;
        private static string voteMaster;
        private static long warpTimeout;
        private static long voteTimeout;
        private static Dictionary<string, bool> voteList;
        private static List<string> ignoreList;
        private static object mcwLock = new object();

        //MCW_LOWEST
        private static Dictionary<string, PlayerWarpRate> warpList = new Dictionary<string, PlayerWarpRate>();

        private const float MAX_VOTE_TIME = 30f;
        private const float MAX_WARP_TIME = 120f;

        private static int voteNeededCount
        {
            get
            {
                return (Server.playerCount + 1) / 2;
            }
        }
        private static int voteFailedCount
        {
            get
            {
                return voteNeededCount + (1 - (voteNeededCount % 2));
            }
        }

        public static void SendAllReportedSkewRates(ClientObject client)
        {
            foreach (ClientObject otherClient in ClientHandler.GetClients())
            {
                if (otherClient.authenticated)
                {
                    if (otherClient != client)
                    {
                        ServerMessage newMessage = new ServerMessage();
                        newMessage.type = ServerMessageType.WARP_CONTROL;
                        using (MessageWriter mw = new MessageWriter())
                        {
                            mw.Write<int>((int)WarpMessageType.REPORT_RATE);
                            mw.Write<string>(otherClient.playerName);
                            mw.Write<float>(otherClient.subspaceRate);
                            newMessage.data = mw.GetMessageBytes();
                        }
                        ClientHandler.SendToClient(client, newMessage, true);
                    }
                }
            }
        }

        public static void HandleWarpControl(ClientObject client, byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData))
            {
                WarpMessageType warpType = (WarpMessageType)mr.Read<int>();
                switch (warpType)
                {
                    case WarpMessageType.REQUEST_CONTROLLER:
                        {
                            HandleRequestController(client);
                        }
                        break;
                    case WarpMessageType.RELEASE_CONTROLLER:
                        {
                            HandleReleaseController(client);
                        }
                        break;
                    case WarpMessageType.REPLY_VOTE:
                        {
                            bool voteReply = mr.Read<bool>();
                            HandleReplyVote(client, voteReply);
                        }
                        break;
                    case WarpMessageType.NEW_SUBSPACE:
                        {
                            long serverTime = mr.Read<long>();
                            double planetTime = mr.Read<double>();
                            float subspaceRate = mr.Read<float>();
                            HandleNewSubspace(client, serverTime, planetTime, subspaceRate);
                        }
                        break;
                    case WarpMessageType.CHANGE_SUBSPACE:
                        {
                            int newSubspace = mr.Read<int>();
                            HandleChangeSubspace(client, newSubspace);
                        }
                        break;
                    case WarpMessageType.REPORT_RATE:
                        {
                            float newSubspaceRate = mr.Read<float>();
                            HandleReportRate(client, newSubspaceRate);
                        }
                        break;
                    case WarpMessageType.CHANGE_WARP:
                        {
                            bool physWarp = mr.Read<bool>();
                            int rateIndex = mr.Read<int>();
                            long serverClock = mr.Read<long>();
                            double planetTime = mr.Read<double>();
                            HandleChangeWarp(client, physWarp, rateIndex, serverClock, planetTime);
                        }
                        break;
                        #if DEBUG
                    default:
                        throw new NotImplementedException("Warp type not implemented");
                        #endif
                }
            }
        }

        private static void HandleRequestController(ClientObject client)
        {
            lock (mcwLock)
            {
                if (Settings.settingsStore.warpMode == WarpMode.MCW_FORCE)
                {
                    if (warpMaster == null)
                    {
                        warpMaster = client.playerName;
                        warpTimeout = DateTime.UtcNow.Ticks + (long)(MAX_WARP_TIME * 10000000);
                        SendSetController(client.playerName, warpTimeout);
                    }
                }
                if (Settings.settingsStore.warpMode == WarpMode.MCW_VOTE)
                {
                    if (voteMaster == null)
                    {
                        if (Server.playerCount == 1)
                        {
                            warpMaster = client.playerName;
                            warpTimeout = DateTime.UtcNow.Ticks + (long)(MAX_WARP_TIME * 10000000);
                            SendSetController(client.playerName, warpTimeout);
                        }
                        else
                        {
                            voteList = new Dictionary<string, bool>();
                            voteMaster = client.playerName;
                            voteTimeout = DateTime.UtcNow.Ticks + (long)(MAX_VOTE_TIME * 10000000);
                            SendRequestVote(client.playerName, voteTimeout);
                        }
                    }
                }
            }
        }

        private static void HandleReleaseController(ClientObject client)
        {
            lock (mcwLock)
            {
                if (Settings.settingsStore.warpMode == WarpMode.MCW_FORCE)
                {
                    if (warpMaster == client.playerName)
                    {
                        warpMaster = null;
                        warpTimeout = long.MinValue;
                        SendSetController(null, long.MinValue);
                    }
                }
                if (Settings.settingsStore.warpMode == WarpMode.MCW_VOTE)
                {
                    if (voteMaster == client.playerName || warpMaster == client.playerName)
                    {
                        voteMaster = null;
                        warpMaster = null;
                        voteTimeout = long.MinValue;
                        warpTimeout = long.MinValue;
                        SendSetController(null, long.MinValue);
                    }
                }
            }
        }

        private static void HandleReplyVote(ClientObject client, bool voteReply)
        {
            if (voteList == null)
            {
                return;
            }
            lock (mcwLock)
            {
                voteList[client.playerName] = voteReply;
                int voteYesCount = 0;
                int voteNoCount = 0;
                foreach (bool vote in voteList.Values)
                {
                    if (vote)
                    {
                        voteYesCount++;
                    }
                    else
                    {
                        voteNoCount++;
                    }
                }
                ServerMessage newMessage = new ServerMessage();
                newMessage.type = ServerMessageType.WARP_CONTROL;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<int>((int)WarpMessageType.REPLY_VOTE);
                    mw.Write<int>(voteYesCount);
                    mw.Write<int>(voteNoCount);
                    newMessage.data = mw.GetMessageBytes();
                }
                ClientHandler.SendToAll(null, newMessage, true);
                if (voteYesCount >= voteNeededCount)
                {
                    warpTimeout = DateTime.UtcNow.Ticks + (long)(MAX_WARP_TIME * 10000000);
                    warpMaster = voteMaster;
                    voteList = null;
                    voteMaster = null;
                    SendSetController(warpMaster, warpTimeout);
                }
                else
                {
                    if (voteNoCount >= voteFailedCount)
                    {
                        voteList = null;
                        voteMaster = null;
                        SendSetController(null, long.MinValue);
                    }
                }
            }
        }

        public static void CheckTimer()
        {
            if (Settings.settingsStore.warpMode == WarpMode.MCW_FORCE || Settings.settingsStore.warpMode == WarpMode.MCW_VOTE)
            {
                if (voteMaster != null)
                {
                    if (DateTime.UtcNow.Ticks > voteTimeout)
                    {
                        voteList = null;
                        voteMaster = null;
                        voteTimeout = long.MinValue;
                        SendSetController(null, long.MinValue);
                    }
                }
                if (warpMaster != null)
                {
                    if (DateTime.UtcNow.Ticks > warpTimeout)
                    {
                        warpMaster = null;
                        warpTimeout = long.MinValue;
                        SendSetController(null, long.MinValue);
                    }
                }
            }
        }

        private static void HandleNewSubspace(ClientObject client, long serverClock, double planetTime, float subspaceSpeed)
        {
            lock (createLock)
            {
                DarkLog.Debug("Create subspace");
                //Create subspace
                Subspace newSubspace = new Subspace();
                newSubspace.serverClock = serverClock;
                newSubspace.planetTime = planetTime;
                newSubspace.subspaceSpeed = subspaceSpeed;
                subspaces.Add(freeID, newSubspace);
                //Create message
                ServerMessage newMessage = new ServerMessage();
                newMessage.type = ServerMessageType.WARP_CONTROL;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<int>((int)WarpMessageType.NEW_SUBSPACE);
                    mw.Write<int>(freeID);
                    mw.Write<long>(serverClock);
                    mw.Write<double>(planetTime);
                    mw.Write<float>(subspaceSpeed);
                    newMessage.data = mw.GetMessageBytes();
                }
                //Tell all clients about the new subspace
                ClientHandler.SendToAll(null, newMessage, true);
                //Send the client to that subspace
                if (Settings.settingsStore.warpMode == WarpMode.MCW_FORCE || Settings.settingsStore.warpMode == WarpMode.MCW_VOTE || Settings.settingsStore.warpMode == WarpMode.MCW_LOWEST)
                {
                    SendSetSubspaceToAll(freeID);
                }
                else
                {
                    SendSetSubspace(client, freeID);
                }
                freeID++;
                //Save to disk
                SaveLatestSubspace();
            }
        }

        private static void HandleChangeSubspace(ClientObject client, int subspace)
        {
            client.subspace = subspace;
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.WARP_CONTROL;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>((int)WarpMessageType.CHANGE_SUBSPACE);
                mw.Write<string>(client.playerName);
                mw.Write<int>(subspace);
                newMessage.data = mw.GetMessageBytes();
            }
            ClientHandler.SendToAll(client, newMessage, true);
        }

        private static void HandleReportRate(ClientObject client, float newSubspaceRate)
        {
            int reportedSubspace = client.subspace;
            client.subspaceRate = newSubspaceRate;
            //Get minimum rate
            foreach (ClientObject otherClient in ClientHandler.GetClients())
            {
                if (otherClient.authenticated && otherClient.subspace == reportedSubspace)
                {
                    if (otherClient.subspaceRate < newSubspaceRate)
                    {
                        newSubspaceRate = otherClient.subspaceRate;
                    }
                }
            }
            //Bound the rate
            if (newSubspaceRate < 0.3f)
            {
                newSubspaceRate = 0.3f;
            }
            if (newSubspaceRate > 1f)
            {
                newSubspaceRate = 1f;
            }
            //Relock the subspace if the rate is more than 3% out of the average
            if (Math.Abs(subspaces[reportedSubspace].subspaceSpeed - newSubspaceRate) > 0.03f)
            {
                //Update the subspace's epoch to now, so we have a new time to lock from.
                UpdateSubspace(reportedSubspace);
                //Change the subspace speed and report it to the clients
                subspaces[reportedSubspace].subspaceSpeed = newSubspaceRate;
                ServerMessage relockMessage = new ServerMessage();
                relockMessage.type = ServerMessageType.WARP_CONTROL;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<int>((int)WarpMessageType.RELOCK_SUBSPACE);
                    mw.Write<int>(reportedSubspace);
                    mw.Write<long>(subspaces[reportedSubspace].serverClock);
                    mw.Write<double>(subspaces[reportedSubspace].planetTime);
                    mw.Write<float>(subspaces[reportedSubspace].subspaceSpeed);
                    relockMessage.data = mw.GetMessageBytes();
                }
                ClientHandler.SendToAll(null, relockMessage, true);
                //Save to disk
                SaveLatestSubspace();
            }
            //Tell other players about the reported rate
            ServerMessage reportMessage = new ServerMessage();
            reportMessage.type = ServerMessageType.WARP_CONTROL;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>((int)WarpMessageType.REPORT_RATE);
                mw.Write<string>(client.playerName);
                mw.Write<float>(client.subspaceRate);
                reportMessage.data = mw.GetMessageBytes();
            }
            ClientHandler.SendToAll(client, reportMessage, true);
        }

        private static void HandleChangeWarp(ClientObject client, bool physWarp, int rateIndex, long serverClock, double planetTime)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.WARP_CONTROL;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>((int)WarpMessageType.CHANGE_WARP);
                mw.Write<string>(client.playerName);
                mw.Write<bool>(physWarp);
                mw.Write<int>(rateIndex);
                mw.Write<long>(serverClock);
                mw.Write<double>(planetTime);
                newMessage.data = mw.GetMessageBytes();
            }
            if (Settings.settingsStore.warpMode == WarpMode.MCW_LOWEST)
            {
                PlayerWarpRate clientWarpRate = null;

                if (!warpList.ContainsKey(client.playerName))
                {
                    clientWarpRate = new PlayerWarpRate();
                    warpList.Add(client.playerName, clientWarpRate);
                }
                else
                {
                    clientWarpRate = warpList[client.playerName];
                }
                clientWarpRate.isPhysWarp = physWarp;
                clientWarpRate.rateIndex = rateIndex;
                clientWarpRate.serverClock = serverClock;
                clientWarpRate.planetTime = planetTime;
                HandleLowestRateChange(client);
            }
            ClientHandler.SendToAll(client, newMessage, true);
        }

        private static void HandleLowestRateChange(ClientObject client)
        {
            string newWarpMaster = null;
            PlayerWarpRate lowestRate = null;

            if (warpMaster != null && warpList.ContainsKey(warpMaster))
            {
                newWarpMaster = warpMaster;
                lowestRate = warpList[warpMaster];
            }
            else
            {
                newWarpMaster = client.playerName;
                lowestRate = warpList[client.playerName];
            }

            foreach (ClientObject testClient in ClientHandler.GetClients())
            {
                if (!warpList.ContainsKey(testClient.playerName))
                {
                    newWarpMaster = null;
                    break;
                }

                PlayerWarpRate pwr = warpList[testClient.playerName];

                if (pwr.rateIndex == 0)
                {
                    newWarpMaster = null;
                    break;
                }
                if (pwr.isPhysWarp != lowestRate.isPhysWarp)
                {
                    newWarpMaster = null;
                    break;
                }
                if (pwr.rateIndex < lowestRate.rateIndex)
                {
                    newWarpMaster = testClient.playerName;
                    lowestRate = pwr;
                }
            }

            if (newWarpMaster != warpMaster)
            {
                //No expire time
                SendSetController(newWarpMaster, long.MinValue);
                warpMaster = newWarpMaster;

            }
        }

        public static void SendAllSubspaces(ClientObject client)
        {
            //Send all the locks.
            foreach (KeyValuePair<int, Subspace> subspace in subspaces)
            {
                ServerMessage newMessage = new ServerMessage();
                newMessage.type = ServerMessageType.WARP_CONTROL;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<int>((int)WarpMessageType.NEW_SUBSPACE);
                    mw.Write<int>(subspace.Key);
                    mw.Write<long>(subspace.Value.serverClock);
                    mw.Write<double>(subspace.Value.planetTime);
                    mw.Write<float>(subspace.Value.subspaceSpeed);
                    newMessage.data = mw.GetMessageBytes();
                }
                ClientHandler.SendToClient(client, newMessage, true);
            }
            //Tell the player "when" everyone is.
            foreach (ClientObject otherClient in ClientHandler.GetClients())
            {
                if (otherClient.authenticated && (otherClient.playerName != client.playerName))
                {
                    ServerMessage newMessage = new ServerMessage();
                    newMessage.type = ServerMessageType.WARP_CONTROL;
                    using (MessageWriter mw = new MessageWriter())
                    {
                        mw.Write<int>((int)WarpMessageType.CHANGE_SUBSPACE);
                        mw.Write<string>(otherClient.playerName);
                        mw.Write<int>(otherClient.subspace);
                        newMessage.data = mw.GetMessageBytes();
                    }
                    ClientHandler.SendToClient(client, newMessage, true);
                }
            }
        }

        private static void SendRequestVote(string playerName, long expireTime)
        {
            if (Settings.settingsStore.warpMode == WarpMode.MCW_FORCE || Settings.settingsStore.warpMode == WarpMode.MCW_VOTE)
            {
                ServerMessage newMessage = new ServerMessage();
                newMessage.type = ServerMessageType.WARP_CONTROL;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<int>((int)WarpMessageType.REQUEST_VOTE);
                    mw.Write<string>(playerName);
                    mw.Write<long>(expireTime);
                    newMessage.data = mw.GetMessageBytes();
                }
                ClientHandler.SendToAll(null, newMessage, true);
            }
        }

        private static void SendSetController(string playerName, long expireTime)
        {
            if (Settings.settingsStore.warpMode == WarpMode.MCW_FORCE || Settings.settingsStore.warpMode == WarpMode.MCW_VOTE || Settings.settingsStore.warpMode == WarpMode.MCW_LOWEST)
            {
                ServerMessage newMessage = new ServerMessage();
                newMessage.type = ServerMessageType.WARP_CONTROL;
                using (MessageWriter mw = new MessageWriter())
                {
                    mw.Write<int>((int)WarpMessageType.SET_CONTROLLER);
                    mw.Write<string>(playerName);
                    mw.Write<long>(expireTime);
                    newMessage.data = mw.GetMessageBytes();
                }
                ClientHandler.SendToAll(null, newMessage, true);
            }
        }

        public static void SendSetSubspace(ClientObject client)
        {
            if (!Settings.settingsStore.keepTickingWhileOffline && ClientHandler.GetClients().Length == 1)
            {
                DarkLog.Debug("Reverting server time to last player connection");
                long currentTime = DateTime.UtcNow.Ticks;
                foreach (KeyValuePair<int, Subspace> subspace in subspaces)
                {
                    subspace.Value.serverClock = currentTime;
                    subspace.Value.subspaceSpeed = 1f;
                }
                SaveLatestSubspace();
            }
            int targetSubspace = -1;
            if (Settings.settingsStore.sendPlayerToLatestSubspace || !offlinePlayerSubspaces.ContainsKey(client.playerName))
            {
                targetSubspace = GetLatestSubspace();
            }
            else
            {
                DarkLog.Debug("Sending " + client.playerName + " to the previous subspace " + targetSubspace);
                targetSubspace = offlinePlayerSubspaces[client.playerName];
            }
            SendSetSubspace(client, targetSubspace);
        }

        public static void SendSetSubspace(ClientObject client, int subspace)
        {
            DarkLog.Debug("Sending " + client.playerName + " to subspace " + subspace);
            client.subspace = subspace;
            if (!Settings.settingsStore.sendPlayerToLatestSubspace)
            {
                offlinePlayerSubspaces[client.playerName] = subspace;
            }
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.SET_SUBSPACE;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>(subspace);
                newMessage.data = mw.GetMessageBytes();
            }
            ClientHandler.SendToClient(client, newMessage, true);
            //Tell everyone else they changed
            ServerMessage changeMessage = new ServerMessage();
            changeMessage.type = ServerMessageType.WARP_CONTROL;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>((int)WarpMessageType.CHANGE_SUBSPACE);
                mw.Write<string>(client.playerName);
                mw.Write<int>(subspace);
                changeMessage.data = mw.GetMessageBytes();
            }
            ClientHandler.SendToAll(client, changeMessage, true);
        }

        public static void SendSetSubspaceToAll(int subspace)
        {
            DarkLog.Debug("Sending everyone to subspace " + subspace);
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.SET_SUBSPACE;
            using (MessageWriter mw = new MessageWriter())
            {
                mw.Write<int>(subspace);
                newMessage.data = mw.GetMessageBytes();
            }
            ClientHandler.SendToAll(null, newMessage, true);
            //Tell everyone else they changed
            foreach (ClientObject otherClient in ClientHandler.GetClients())
            {
                if (otherClient.authenticated)
                {
                    ServerMessage changeMessage = new ServerMessage();
                    changeMessage.type = ServerMessageType.WARP_CONTROL;
                    using (MessageWriter mw = new MessageWriter())
                    {
                        mw.Write<int>((int)WarpMessageType.CHANGE_SUBSPACE);
                        mw.Write<string>(otherClient.playerName);
                        mw.Write<int>(subspace);
                        changeMessage.data = mw.GetMessageBytes();
                    }
                    ClientHandler.SendToAll(otherClient, changeMessage, true);
                }
            }
        }

        private static void LoadSavedSubspace()
        {
            try
            {
                string subspaceFile = Path.Combine(Server.universeDirectory, "subspace.txt");
                using (StreamReader sr = new StreamReader(subspaceFile))
                {
                    //Ignore the comment line.
                    string firstLine = "";
                    while (firstLine.StartsWith("#") || String.IsNullOrEmpty(firstLine))
                    {
                        firstLine = sr.ReadLine().Trim();
                    }
                    Subspace savedSubspace = new Subspace();
                    int subspaceID = Int32.Parse(firstLine);
                    savedSubspace.serverClock = Int64.Parse(sr.ReadLine().Trim());
                    savedSubspace.planetTime = Double.Parse(sr.ReadLine().Trim());
                    savedSubspace.subspaceSpeed = Single.Parse(sr.ReadLine().Trim());
                    subspaces.Add(subspaceID, savedSubspace);
                    lock (createLock)
                    {
                        freeID = subspaceID + 1;
                    }
                }
            }
            catch
            {
                DarkLog.Debug("Creating new subspace lock file");
                Subspace newSubspace = new Subspace();
                newSubspace.serverClock = DateTime.UtcNow.Ticks;
                newSubspace.planetTime = 100d;
                newSubspace.subspaceSpeed = 1f;
                subspaces.Add(0, newSubspace);
                SaveSubspace(0, newSubspace);
            }
        }

        public static int GetLatestSubspace()
        {
            int latestID = 0;
            double latestPlanetTime = 0;
            long currentTime = DateTime.UtcNow.Ticks;
            foreach (KeyValuePair<int,Subspace> subspace in subspaces)
            {
                double currentPlanetTime = subspace.Value.planetTime + (((currentTime - subspace.Value.serverClock) / 10000000) * subspace.Value.subspaceSpeed);
                if (currentPlanetTime > latestPlanetTime)
                {
                    latestID = subspace.Key;
                }
            }
            return latestID;
        }

        private static void SaveLatestSubspace()
        {
            int latestID = GetLatestSubspace();
            SaveSubspace(latestID, subspaces[latestID]);
        }

        private static void UpdateSubspace(int subspaceID)
        {
            //New time = Old time + (seconds since lock * subspace rate)
            long newServerClockTime = DateTime.UtcNow.Ticks;
            float timeSinceLock = (DateTime.UtcNow.Ticks - subspaces[subspaceID].serverClock) / 10000000f;
            double newPlanetariumTime = subspaces[subspaceID].planetTime + (timeSinceLock * subspaces[subspaceID].subspaceSpeed);
            subspaces[subspaceID].serverClock = newServerClockTime;
            subspaces[subspaceID].planetTime = newPlanetariumTime;
        }

        private static void SaveSubspace(int subspaceID, Subspace subspace)
        {
            string subspaceFile = Path.Combine(Server.universeDirectory, "subspace.txt");
            using (StreamWriter sw = new StreamWriter(subspaceFile))
            {
                sw.WriteLine("#Incorrectly editing this file will cause weirdness. If there is any errors, the universe time will be reset.");
                sw.WriteLine("#This file can only be edited if the server is stopped.");
                sw.WriteLine("#Each variable is on a new line. They are subspaceID, server clock (from DateTime.UtcNow.Ticks), universe time, and subspace speed.");
                sw.WriteLine(subspaceID);
                sw.WriteLine(subspace.serverClock);
                sw.WriteLine(subspace.planetTime);
                sw.WriteLine(subspace.subspaceSpeed);
            }
        }

        internal static void HoldSubspace()
        {
            //When the last player disconnects and we are a no-tick-offline server, save the universe time.
            UpdateSubspace(GetLatestSubspace());
            SaveLatestSubspace();
        }

        internal static void DisconnectPlayer(string playerName)
        {
            if (warpList.ContainsKey(playerName))
            {
                warpList.Remove(playerName);
            }
            if (voteList != null)
            {
                if (voteList.ContainsKey(playerName))
                {
                    voteList.Remove(playerName);
                }
            }
            if (ignoreList != null)
            {
                if (ignoreList.Contains(playerName))
                {
                    ignoreList.Remove(playerName);
                }
            }
            if (warpMaster == playerName || voteMaster == playerName)
            {
                SendSetController(null, long.MinValue);
            }
        }

        public static void Reset()
        {
            subspaces.Clear();
            offlinePlayerSubspaces.Clear();
            warpList.Clear();
            ignoreList = null;
            LoadSavedSubspace();
        }
    }
}

