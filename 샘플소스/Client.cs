using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Text;

public class Client : MonoBehaviour {


	public connstate_t connectState;
	public float connectTime = 0;
	int connectPacketCount = 0;
	bool newSnapshots = false;
	int serverID = 0;
	long serverTimeDelta = 0;
	bool extrapolatedSnapshot = false;
	long oldServerTime = 0;

	CommandManager commandManager = new CommandManager();
	PredictManager predictManager = new PredictManager();
	SnapshotManager snapshotManager = new SnapshotManager();


	public netchan_t NetChannel = new netchan_t();
	public GameManager gameManager;
	public TutorialInfo tutorialInfo;

	long clientTime;
	public static long ServerTime;
	int RESET_TIME = 500;


	public void Frame(long msec)
	{
		clientTime += msec;

		SendCmd();
		CheckForResend();

		if (connectState == connstate_t.CA_PRIMED)
		{
			if (newSnapshots)
			{
				newSnapshots = false;
				FirstSnapshot();
			}
		}

		if (connectState == connstate_t.CA_ACTIVE)
		{
			ServerTime = clientTime + serverTimeDelta;

			if (Server.IsServerRunning == false)
			{
				Dbg.Trace(">>>>>> Frame():ServerTime:" + ServerTime +"/ctime:" + clientTime +"/delta:" + serverTimeDelta);
			}

			if (ServerTime < oldServerTime)
			{
				ServerTime = oldServerTime;

				Dbg.Trace(">>>>>> Frame():serverTime is old /newstime:" + ServerTime );
			}
			oldServerTime = ServerTime;

			if (Server.IsServerRunning == false)
			{
				if (snapshotManager.lastestSnap != null && clientTime + serverTimeDelta >= snapshotManager.lastestSnap.serverTime - 5)
				{
					long time = clientTime + serverTimeDelta;
					Dbg.Trace(">>>>>> Frame():time need exp /stime:" + time + "/snapTime:" + snapshotManager.lastestSnap.serverTime);
					extrapolatedSnapshot = true;
				}

				if (newSnapshots)
				{
					AdjustTimeDelta();
				}
			}


		}

		if (Server.IsServerRunning == false && connectState >= connstate_t.CA_PRIMED)
		{
			snapshotManager.CG_ProcessSnapshots();
			predictManager.CG_PredictPlayerState(snapshotManager.snap, snapshotManager.nextSnap);
			snapshotManager.AdaptSnapshot(gameManager.m_Tanks);
		}
	}

	void FirstSnapshot( )
	{
		connectState = connstate_t.CA_ACTIVE;

		serverTimeDelta = snapshotManager.lastestSnap.serverTime - clientTime;
		oldServerTime = snapshotManager.lastestSnap.serverTime;

		Dbg.Trace("========= FirstSnapshot():CA_ACTIVE/serverTimeDelta/stime:" + snapshotManager.lastestSnap.serverTime
			+"/ctime:" +clientTime + "/delta:" + serverTimeDelta);
	}

	
	void AdjustTimeDelta() {
		long		newDelta;
		float		deltaDelta;

		newDelta = snapshotManager.lastestSnap.serverTime - clientTime;
		deltaDelta = Mathf.Abs(newDelta - serverTimeDelta);

		if ( deltaDelta > RESET_TIME ) {
			serverTimeDelta = newDelta;
			oldServerTime = snapshotManager.lastestSnap.serverTime;	// FIXME: is this a problem for cgame?
			ServerTime = snapshotManager.lastestSnap.serverTime;

			Dbg.Trace(">>>>>> Frame():AdjustTimeDelta() ADJUST /delta:" + serverTimeDelta + "/oldServerTime:" + oldServerTime + "/ServerTime" + ServerTime);

		} else if ( deltaDelta > 100 ) {
			// fast adjust, cut the difference in half

			serverTimeDelta = ( serverTimeDelta + newDelta ) >> 1;

			Dbg.Trace(">>>>>> Frame():AdjustTimeDelta() FAST /delta:" + serverTimeDelta);
		} else {
			// slow drift adjust, only move 1 or 2 msec

			// if any of the frames between this and the previous snapshot
			// had to be extrapolated, nudge our sense of time back a little
			// the granularity of +1 / -2 is too high for timescale modified frametimes
			if ( extrapolatedSnapshot ) {
				extrapolatedSnapshot = false;
				serverTimeDelta -= 2;

				Dbg.Trace(">>>>>> Frame():AdjustTimeDelta() EXTRA /delta:" + serverTimeDelta);
			} else {
				// otherwise, move our sense of time forward to minimize total latency
				serverTimeDelta++;

				Dbg.Trace(">>>>>> Frame():AdjustTimeDelta() EXTRA /delta:" + serverTimeDelta);
			}
		}

	}


    public void PacketProcess(netadr_t from, msg_t msg)
    {
        string msgString = Encoding.UTF8.GetString(msg.data, 0, msg.cursize);
		string[] splitMsg = msgString.Split(NetSys.msgToken);

        if (msg.cursize >= 4 && (int)msg.data[0] == NetSys.connectionlessChar)
        {
            msgString = msgString.Remove(0, 4);
            ConnectionlessPacket(from, msgString);
            return;
        }

		if (connectState < connstate_t.CA_CONNECTED)
		{
			return;		// can't be a valid sequenced packet
		}

		if (msg.cursize < 4)
		{
			Debug.LogWarning(from.ToString() + " : Runt packet");
			return;
		}

		if (from.IsSame(NetChannel.remoteAddress) == false)
		{
			Debug.LogWarning( from.ToString() + ":sequenced packet without connection");
			return;
		}

		if (Server.IsServerRunning == false)
		{
			Dbg.Trace("========= ClientPacketProcess()/from:" + from.ToString() + "/msg:" + msgString);
		}

		ParseServerMessage(splitMsg);
    }

	void ParseServerMessage(string[] splitMsg)
	{
		NetChannel.incomingSequence = int.Parse(splitMsg[0]);
		string cmd = splitMsg[1];

		if( cmd == "svc_gamestate")
		{
			ParseGamestate(splitMsg);
		}
		else if(cmd == "svc_snapshot")
		{
			ParseSnapshot(splitMsg);
		}
	}

	void ParseGamestate(string[] splitMsg)
	{
		// init game
		serverID = int.Parse(splitMsg[2]);

		if (Server.IsServerRunning == false)
		{
			int count = int.Parse(splitMsg[3]);
			for (int i = 0; i < count; ++i)
			{
				string name = splitMsg[4 + i];
				gameManager.SpawnTank(int.Parse(name));
			}
		}

		tutorialInfo.StartGame();
		if (NetCode.IsServerRunning == false)
		{
			gameManager.RoundStart();
		}

		connectState = connstate_t.CA_PRIMED;

		Dbg.Trace("========= ParseGamestate():CA_PRIMED");
	}

	void ParseSnapshot(string[] splitMsg)
	{
		snapshot_t newSnap = snapshot_t.Parse(splitMsg);

		newSnap.sequenceNumber = NetChannel.incomingSequence;
		snapshotManager.AddSnapshot(newSnap);
		newSnapshots = true;
	}

    public void FindServer()
    {
        netadr_t to = new netadr_t();
        string message = string.Empty;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += "getinfo"+NetSys.msgToken;

        to.type = netadrtype_t.NA_BROADCAST;
        for (int i = 0; i < 4; ++i)
        {
            int port = NetSys.PORT_SERVER + i;
            to.port = (ushort)port;
            NetSys.Instance.SendPacket(netsrc_t.NS_CLIENT, message.Length, message, to);
        }
    }

    public void Join( netadr_t serverAddr )
    {
		Dbg.Trace("========= Join():CA_CHALLENGING/addr:" + serverAddr.ToString());
        connectState = connstate_t.CA_CHALLENGING;
        connectTime = -99999;	// CheckForResend() will fire immediately
        connectPacketCount = 0;
		NetChannel.remoteAddress = serverAddr;
    }

    void ConnectionlessPacket(netadr_t from, string msg)
    {

        string[] splitMsg = msg.Split(NetSys.msgToken);

        if (splitMsg[0] == "infoResponse")
        {
            tutorialInfo.AddServerItem(from.ToString());
        }
        else if (splitMsg[0] == "connectResponse")
        {
            connectState = connstate_t.CA_CONNECTED;
			Dbg.Trace("========= ConnectionlessPacket():Recv connectResponse/CA_CONNECTED");
        }
    }

    public void CheckForResend()
    {
        if (connectState != connstate_t.CA_CHALLENGING)
            return;

		if (clientTime - connectTime < NetSys.RETRANSMIT_TIMEOUT)
        {
            return;
        }

		connectTime = clientTime;	// for retransmit requests
        connectPacketCount++;

        string message = string.Empty;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += "connect" + NetSys.msgToken;
		message += NetChannel.qport.ToString() + NetSys.msgToken;

		NetSys.Instance.SendPacket(netsrc_t.NS_CLIENT, message.Length, message, NetChannel.remoteAddress);
    }

    void SendCmd()
    {
        if (connectState < connstate_t.CA_CONNECTED)
            return;

		if (connectState >= connstate_t.CA_PRIMED)
		{
			commandManager.CreateNewCommands();
		}

		WritePacket();
    }


	void WritePacket()
	{
		string message = string.Empty;
		message += NetChannel.outgoingSequence.ToString() + NetSys.msgToken; 
		message += NetChannel.qport.ToString() + NetSys.msgToken; 
		message += serverID.ToString() + NetSys.msgToken; 
		message += NetChannel.incomingSequence.ToString() + NetSys.msgToken; 

		// add client command
		message += commandManager.Write();

		if (Server.IsServerRunning == false)
		{
			Dbg.Trace("========= WritePacket():" + message);
		}
	

		++NetChannel.outgoingSequence;
		NetSys.Instance.SendPacket(netsrc_t.NS_CLIENT, message.Length, message, NetChannel.remoteAddress);
	}






}
