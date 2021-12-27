using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Driver : MonoBehaviour
{
    // Start is called before the first frame update
    IEnumerator Start()
    {
        WebSocketServer server = new WebSocketServer();
        server.SetLogCallback(Debug.Log);

        server.onClientConnect += (int id)=>Debug.Log("Client " + id + " connected");
        server.onClientMessage += (int id, byte[] msg, int opcode)=>{
            Debug.Log("Client " + id + " send a message with opcode " + opcode);
            if (opcode == 1) Debug.Log("String message: " + System.Text.Encoding.ASCII.GetString(msg));
            server.Send(id, "Pong", 1);
        };
        server.onClientDisconnect += (int id)=>Debug.Log("Client " + id + " disconnected");

        server.Start(8686);

        yield return new WaitForSeconds(3);
        server.Stop();
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
