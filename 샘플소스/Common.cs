using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Text;

public class Common : MonoBehaviour {

 	float maxFrame = 30;
	long clampTime;
	long clClampTime;
	long svClampTime;
	long minMsec;
	long msec;
	long com_frameTime;
	long lastTime;
	long sys_timeBase;
	bool initialized = false;

	public Client client;
	public Server server;
	
	private static NetCode _instance;
	public static NetCode Instance
	{
		get
		{
			if (!_instance)
			{
				_instance = GameObject.FindObjectOfType(typeof(NetCode)) as NetCode;
				if (!_instance)
				{
					GameObject container = new GameObject();
					container.name = "NetCode";
					_instance = container.AddComponent(typeof(NetCode)) as NetCode;
				}
			}

			return _instance;
		}
	}


	void Start()
	{
		client.NetChannel.qport = Random.Range(1, 0xffff);

		float second = 1000 / maxFrame;
		minMsec = (long)second;

		clClampTime = 5000;
		svClampTime = 200;

	}


    void Update()
    {
        netadr_t net_from = new netadr_t();
        msg_t net_message = new msg_t();


		while (NetSys.Instance.GetLoopPacket(netsrc_t.NS_CLIENT, ref net_from, ref net_message))
		{
			client.PacketProcess(net_from, net_message);
		}

		while (NetSys.Instance.GetLoopPacket(netsrc_t.NS_SERVER, ref net_from, ref net_message))
		{
			if (Server.IsServerRunning)
			{
				server.PacketProcess(net_from, net_message);
			}
		}


		while (true)
		{
			net_message.cursize = 0;
			if (NetSys.Instance.Sys_GetPacket(ref net_from, ref net_message))
			{
				PacketProcess(net_from, net_message);
			}

			if (net_message.cursize == 0)
				break;
		}


		do
		{
			com_frameTime = Sys_Milliseconds();
			if (lastTime > com_frameTime)
			{
				lastTime = com_frameTime;		// possible on first frame
			}
			msec = com_frameTime - lastTime;
		} while (msec < minMsec);
		lastTime = com_frameTime;



		if (Server.IsServerRunning == false)
		{
			// clients of remote servers do not want to clamp time, because
			// it would skew their view of the server's time temporarily
			clampTime = clClampTime;
		}
		else
		{
			// for local single player gaming
			// we may want to clamp the time to prevent players from
			// flying off edges when something hitches.
			clampTime = svClampTime;
		}

		if (msec > clampTime)
		{
			msec = clampTime;
		}



		if (Server.IsServerRunning)
		{
			server.Frame(msec);
		}
		client.Frame(msec);
    }

    void PacketProcess(netadr_t from, msg_t msg)
    {
        string msgString = Encoding.UTF8.GetString(msg.data, 0, msg.cursize);
        if (NetSys.LogOn)
        {
            Dbg.Trace("=========== RecvPacket ================ ");
            Dbg.Trace("Type : " + from.type.ToString());
            Dbg.Trace("Address : " + from.ToString());
            Dbg.Trace("Message : " + msgString);
        }

		if (Server.IsServerRunning)
        {
            server.PacketProcess(from, msg);
        }
        else
        {
            client.PacketProcess(from, msg);
        }
    }


	long Sys_Milliseconds ()
	{
		long			sys_curtime;

		if (!initialized) {
			sys_timeBase = System.DateTime.Now.Ticks / System.TimeSpan.TicksPerMillisecond;
			initialized = true;
		}
		sys_curtime = System.DateTime.Now.Ticks / System.TimeSpan.TicksPerMillisecond - sys_timeBase;

		return sys_curtime;
	}



	public static void Pmove(usercmd_t cmd, TankManager tank, client_t cl)
	{
		long finalTime;
		long movemsec;
		long comTime = cmd.serverTime;

		finalTime = comTime;

		if (finalTime < cl.lastUsercmd.serverTime)
		{
			return;	// should not happen
		}

		if (finalTime > cl.lastUsercmd.serverTime + 1000)
		{
			cl.lastUsercmd.serverTime = finalTime - 1000;
		}


		Dbg.Trace("========= Pmove()/cmdstime:" + comTime + "/lastCmdStime:" + cl.lastUsercmd.serverTime
			+ "/move:" + cmd.movement + "/turn" + cmd.turn);

		// chop the move up if it is too long, to prevent framerate
		// dependent behavior
		while (cl.lastUsercmd.serverTime != finalTime)
		{
			movemsec = finalTime - cl.lastUsercmd.serverTime;

			if (movemsec > 66)
			{
				movemsec = 66;
			}

			comTime = cl.lastUsercmd.serverTime + movemsec;

			float frameTime = comTime - cl.lastUsercmd.serverTime;
			if (frameTime < 0)
				frameTime = 0;

			frameTime *= 0.001f;
			cl.lastUsercmd.serverTime = comTime;

			tank.m_Movement.Move(cmd.movement, frameTime);
			tank.m_Movement.Turn(cmd.turn, frameTime);
		}

		cl.lastUsercmd = cmd;

	}

}
