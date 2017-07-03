using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Text;

public class Server : MonoBehaviour {


    public static bool IsServerRunning = false;
	List<client_t> clients = new List<client_t>();
	int serverId = 0;
	long serverTime;
	long timeResidual;
	public GameManager gameManager;
	public Client localClient;
	public long minMsec;

    public void StartServer()
    {
		IsServerRunning = true;
		localClient.connectState = connstate_t.CA_CHALLENGING;
		serverId = Random.Range(1, 0xffff);
		localClient.connectTime = -99999;	

		// for loopback client
		localClient.NetChannel.remoteAddress.type = netadrtype_t.NA_LOOPBACK;

		localClient.CheckForResend();

		Dbg.Trace("========= StartServer():CA_CHALLENGING");
    }

    public void StopServer()
    {
		IsServerRunning = false;
    }


	public void Frame(long msec)
	{
		timeResidual += msec;
		// run the game simulation in chunks
		while (timeResidual >= minMsec)
		{
			timeResidual -= minMsec;
			serverTime += minMsec;
		}

		Dbg.Trace(">>>>>> Frame():sTime:" + serverTime);

		SendClientMessages();
	}


    public void PacketProcess(netadr_t from, msg_t msg)
    {
        string msgString = Encoding.UTF8.GetString(msg.data, 0, msg.cursize);
		string[] splitMsg = msgString.Split(NetSys.msgToken);

        if (msg.cursize >= 4 && (int)msg.data[0] == NetSys.connectionlessChar)
        {
			splitMsg[0] = splitMsg[0].Remove(0, 4);
			ConnectionlessPacket(from, splitMsg);
            return;
        }

		int sequenceNumber = int.Parse(splitMsg[0]);
		int qport = int.Parse(splitMsg[1]);

		Dbg.Trace("========= ServerPacket()/from:" + from + "/msg:" + msgString);

		for(int i = 0 ; i < clients.Count ; ++i )
		{
			client_t cl = clients[i];
			if (cl.state == clientState_t.CS_FREE)
			{
				continue;
			}

			if (from.IsSame(cl.netChan.remoteAddress) == false)
			{
				continue;
			}
			// it is possible to have multiple clients from a single IP
			// address, so they are differentiated by the qport variable
			if (cl.netChan.qport != qport)
			{
				continue;
			}

			// the IP port can't be used to differentiate them, because
			// some address translating routers periodically change UDP
			// port assignments
			if (cl.netChan.remoteAddress.port != from.port)
			{
				Dbg.Trace("PacketEvent: fixing up a translated port\n");
				cl.netChan.remoteAddress.port = from.port;
			}

			cl.netChan.incomingSequence = sequenceNumber;
			cl.lastPacketTime = serverTime;	// don't timeout
			ExecuteClientMessage(cl, splitMsg);
			return;

		}

    }

    void ConnectionlessPacket(netadr_t from, string[] splitMsg)
    {
        if (splitMsg[0] == "getinfo")
        {
            SVC_Info(from);
        }
        else if (splitMsg[0] == "connect")
        {
            DirectConnect( from, int.Parse(splitMsg[1]) );
        }
    }

	void ExecuteClientMessage(client_t cl, string[] splitMsg)
	{
		int serverId = int.Parse(splitMsg[2]);
		cl.messageAcknowledge = int.Parse(splitMsg[3]);

		if (serverId != this.serverId)
		{
			if (cl.messageAcknowledge > cl.gamestateMessageNum)
			{
				Debug.LogWarning(cl.userinfo + " : dropped gamestate, resending");
				SendClientGameState(cl);
			}
			return;
		}

		string cmd = splitMsg[4];
		if (cmd == "clc_move")
		{

			UserMove(cl, splitMsg, true);
		}
	}

    void SendClientGameState(client_t client)
    {
		Dbg.Trace("========= SendClientGameState():CS_PRIMED/qport:" + client.netChan.qport);

        client.state = clientState_t.CS_PRIMED;
		client.gamestateMessageNum = client.netChan.outgoingSequence;

		string msg = string.Empty;
		msg += "svc_gamestate" + NetSys.msgToken;
		msg += serverId.ToString() + NetSys.msgToken;
		msg += clients.Count.ToString() + NetSys.msgToken;
		for (int i = 0; i < clients.Count; ++i )
		{
			msg += clients[i].userinfo + NetSys.msgToken;
		}

		WritePacket(client.netChan, msg);
    }

	void UserMove(client_t cl, string[] splitMsg, bool delta)
	{
		int cmdCount = 0;
		List<usercmd_t> cmds = new List<usercmd_t>();
		cmdCount = int.Parse(splitMsg[5]);
		for (int i = 0; i < cmdCount; ++i )
		{
			usercmd_t cmd = new usercmd_t();
			cmd.serverTime = long.Parse(splitMsg[6 + 5*i]);
			cmd.fire = bool.Parse(splitMsg[7 + 5 * i]);
			cmd.fireForce = float.Parse(splitMsg[8 + 5 * i]);
			cmd.movement = float.Parse(splitMsg[9 + 5 * i]);
			cmd.turn = float.Parse(splitMsg[10 + 5 * i]);

			cmds.Add(cmd);
		}

		if (cl.state == clientState_t.CS_PRIMED)
		{
			ClientEnterWorld(cl);
		}

		// usually, the first couple commands will be duplicates
		// of ones we have previously received, but the servertimes
		// in the commands will cause them to be immediately discarded
		for (int i = 0; i < cmdCount; i++)
		{
			// if this is a cmd from before a map_restart ignore it
			if (cmds[i].serverTime > cmds[cmdCount - 1].serverTime)
			{
				continue;
			}

			// don't execute if this is an old cmd which is already executed
			if (cmds[i].serverTime <= cl.lastUsercmd.serverTime)
			{
				Dbg.Trace("========= UserMove():old command !!/qport:" + cl.netChan.qport + "/cmdstime:" + cmds[i].serverTime + "/lastCmdStime:" + cl.lastUsercmd.serverTime
					+ "/move:" + cmds[i].movement + "/turn" + cmds[i].turn);

				continue;
			}
			ClientThink(cl, cmds[i]);
		}
	}

	void ClientEnterWorld(client_t cl)
	{
		Dbg.Trace("========= ClientEnterWorld():CS_ACTIVE/qport:" + cl.netChan.qport);

		cl.state = clientState_t.CS_ACTIVE;

		gameManager.SpawnTank(cl.netChan.qport);

		if(gameManager.tankCount > 1)
		{
			gameManager.RoundStart();
		}
	}


	void ClientThink(client_t cl, usercmd_t cmd)
	{

		if (cl.state != clientState_t.CS_ACTIVE)
		{
			return;		// may have been kicked during the last usercmd
		}

		TankManager tank = gameManager.GetTank(cl.netChan.qport);
		Common.Pmove(cmd, tank, cl);

		if(cmd.fire)
		{
			int id = tank.m_Shooting.Fire(cmd.fireForce);

			RocketInfo info = new RocketInfo();
			info.id = id;
			info.pos = tank.m_Shooting.m_FireTransform.position;
			info.angle = tank.m_Shooting.m_FireTransform.rotation;
			info.fireID = tank.m_PlayerNumber;
			info.force = cmd.fireForce;

			GameManager.Instance.AddRocket(info);
		}
	}



    void SVC_Info(netadr_t from)
    {
        string message = string.Empty;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += "infoResponse" + NetSys.msgToken ;

         NetSys.Instance.SendPacket(netsrc_t.NS_SERVER, message.Length, message, from);
    }

    void DirectConnect(netadr_t from, int qport)
    {
		Dbg.Trace("========= DirectConnect():CS_CONNECTED/qport:" + qport);

        client_t newUser = new client_t();
        newUser.state = clientState_t.CS_CONNECTED;
        newUser.userinfo = qport.ToString();
        newUser.netChan.remoteAddress = from;
        newUser.netChan.qport = qport;

        clients.Add(newUser);

        string message = string.Empty;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += NetSys.connectionlessChar;
        message += "connectResponse" + NetSys.msgToken;

        NetSys.Instance.SendPacket(netsrc_t.NS_SERVER, message.Length, message, from);

    }


	void SendClientMessages()
	{
		client_t c;

		// send a message to each connected client
		for (int i = 0; i < clients.Count; i++)
		{
			c = clients[i];

			if (c.state == clientState_t.CS_FREE)
				continue;		// not connected

			// generate and send a new message
			SendClientSnapshot(c);
			c.lastSnapshotTime = serverTime;
		}
	}

	void SendClientSnapshot(client_t client)
	{
		// make snapshot message
		string snapshotMsg = string.Empty;
		snapshotMsg += "svc_snapshot" + NetSys.msgToken;
		snapshotMsg += serverTime.ToString() + NetSys.msgToken;

		snapshot_t sendSnap = new snapshot_t();
		sendSnap.ps = new playerState_t();

		// 로딩 시기를 알수 없어 일단 클라 connectstate를 사용.		
		if (localClient.connectState >= connstate_t.CA_PRIMED)
		{

			// playerstate
			for (int i = 0; i < gameManager.m_Tanks.Length; ++i)
			{
				TankManager tank = gameManager.m_Tanks[i];

				if (tank.m_PlayerNumber == client.netChan.qport)
				{
					sendSnap.ps.commandTime = client.lastUsercmd.serverTime;
					sendSnap.ps.health = tank.m_tankHealth.m_CurrentHealth;
					sendSnap.ps.pos = tank.m_Movement.GetPosition();
					sendSnap.ps.angle = tank.m_Movement.GetAngle();
					break;
				}
			}

			// entitystate
			int tankCount = gameManager.tankCount - 1;
			if (tankCount < 0)
				tankCount = 0;
			snapshotMsg += tankCount.ToString() + NetSys.msgToken;

			if (tankCount != 0)
			{
				for (int i = 0; i < gameManager.m_Tanks.Length; ++i)
				{
					TankManager tank = gameManager.m_Tanks[i];

					if (tank.m_PlayerNumber == client.netChan.qport)
						continue;

					entityState_t entityState = new entityState_t();
					entityState.type = entType_t.TR_TANK;
					entityState.id = tank.m_PlayerNumber;
					entityState.pos = tank.m_Movement.GetPosition();
					entityState.angle = tank.m_Movement.GetAngle();
					entityState.health = tank.m_tankHealth.m_CurrentHealth;

					sendSnap.entities[sendSnap.numEntities] = entityState;
					sendSnap.numEntities++;
				}

			}

			int rocketCount = GameManager.Instance.m_Rockets.Count;
			if (rocketCount < 0)
				rocketCount = 0;
			snapshotMsg += rocketCount.ToString() + NetSys.msgToken;

			if(rocketCount != 0)
			{
				for (int i = 0; i < GameManager.Instance.m_Rockets.Count; ++i)
				{
					RocketInfo info = GameManager.Instance.m_Rockets[i];
					entityState_t entityState = new entityState_t();
					entityState.type = entType_t.TR_ROCKET;
					entityState.id = info.id;
					entityState.pos = info.pos;
					entityState.angle = info.angle;
					entityState.fireTankID = info.fireID;
					entityState.rocketForce = info.force;

					sendSnap.entities[sendSnap.numEntities] = entityState;
					sendSnap.numEntities++;
				}
			}

		}

		Dbg.Trace("========= SendClientSnapshot()/qport:" + client.netChan.qport + "/sTime:" + serverTime.ToString() + "/msg:" + sendSnap.ToString());

		// send to client
		WritePacket(client.netChan, sendSnap.ToString());
	}


	void WritePacket(netchan_t channel, string msg)
	{
		string message = string.Empty;
		message += channel.outgoingSequence.ToString() + NetSys.msgToken; 
		message += msg;

		++channel.outgoingSequence;

		NetSys.Instance.SendPacket(netsrc_t.NS_SERVER, message.Length, message, channel.remoteAddress);
	}





}
