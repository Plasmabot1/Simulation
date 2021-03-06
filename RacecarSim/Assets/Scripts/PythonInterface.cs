﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Manages UDP communication with the user's Python script.
/// </summary>
public class PythonInterface
{
    #region Constants
    /// <summary>
    /// The UDP port used by the Unity simulation (this program).
    /// </summary>
    private const int unityPort = 5065;

    /// <summary>
    /// The UDP port used by the Unity simulation (this program) for async (Jupyter Notebook) calls.
    /// </summary>
    private const int unityPortAsync = 5064;

    /// <summary>
    /// The IP address used for communication.
    /// </summary>
    private static readonly IPAddress ipAddress = IPAddress.Parse("127.0.0.1");

    /// <summary>
    /// The time (in ms) to wait for Python to respond.
    /// </summary>
    private const int timeoutTime = 5000;

    /// <summary>
    /// The maximum UDP packet size allowed on Windows.
    /// </summary>
    private const int maxPacketSize = 65507;
    #endregion

    #region Public Interface
    /// <summary>
    /// Creates a Python Interface for a player.
    /// </summary>
    /// <param name="racecar">The car which the python interface will control.</param>
    public PythonInterface(Racecar racecar)
    {
        this.wasExitHandled = false;
        this.isSyncConnected = false;
        this.racecar = racecar;

        // Establish and configure a UDP port
        this.udpClient = new UdpClient(new IPEndPoint(PythonInterface.ipAddress, PythonInterface.unityPort));
        this.udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        this.udpClient.Client.ReceiveTimeout = PythonInterface.timeoutTime;

        // Initialize client to handle async calls
        this.asyncClientThread = new Thread(new ThreadStart(this.ProcessAsyncCalls))
        {
            IsBackground = true
        };
        this.asyncClientThread.Start();
    }

    ~PythonInterface()
    {
        this.HandleExit();
    }

    /// <summary>
    /// Closes all UDP clients and sends an exit command to Python if connected.
    /// </summary>
    public void HandleExit()
    {
        if (!this.wasExitHandled)
        {
            if (this.isSyncConnected)
            {
                try
                {
                    this.udpClient.Send(new byte[] { (byte)Header.unity_exit }, 1);
                }
                catch (Exception e)
                {
                    Debug.Log($"Unable to send exit command to Python. Error: {e}");
                }

                this.isSyncConnected = false;
            }

            this.udpClient.Close();
            this.udpClientAsync.Close();
            this.wasExitHandled = true;
        }
    }

    /// <summary>
    /// Tells Python to run the user's start function.
    /// </summary>
    public void PythonStart()
    {
        this.PythonCall(Header.unity_start);
    }

    /// <summary>
    /// Tells Python to run the user's update function.
    /// </summary>
    public void PythonUpdate()
    {
        this.PythonCall(Header.unity_update);
    }
    #endregion

    /// <summary>
    /// Header bytes used in our communication protocol.
    /// </summary>
    private enum Header
    {
        error,
        connect,
        unity_start,
        unity_update,
        unity_exit,
        python_finished,
        python_send_next,
        racecar_go,
        racecar_set_start_update,
        racecar_get_delta_time,
        racecar_set_update_slow_time,
        camera_get_color_image,
        camera_get_depth_image,
        camera_get_width,
        camera_get_height,
        controller_is_down,
        controller_was_pressed,
        controller_was_released,
        controller_get_trigger,
        controller_get_joystick,
        display_show_image,
        drive_set_speed_angle,
        drive_stop,
        drive_set_max_speed,
        lidar_get_num_samples,
        lidar_get_samples,
        physics_get_linear_acceleration,
        physics_get_angular_velocity,
    }

    /// <summary>
    /// True if exit was properly handled.
    /// </summary>
    private bool wasExitHandled;

    #region Sync
    /// <summary>
    /// The racecar controlled by the user's Python script.
    /// </summary>
    private Racecar racecar;

    /// <summary>
    /// True if udpClient is connected to Python.
    /// </summary>
    private bool isSyncConnected;

    /// <summary>
    /// The UDP client used to send packets to Python.
    /// </summary>
    private UdpClient udpClient;

    /// <summary>
    /// The IP address and port used by the Python RACECAR library.
    /// </summary>
    private IPEndPoint pythonEndPoint;

    /// <summary>
    /// Connect the sync client to a Python script.
    /// </summary>
    /// <param name="pythonPort">The port used by the Python script.</param>
    private void ConnectSyncClient(int pythonPort)
    {
        this.pythonEndPoint = new IPEndPoint(PythonInterface.ipAddress, pythonPort);
        this.udpClient.Connect(this.pythonEndPoint);
        this.isSyncConnected = true;
    }

    /// <summary>
    /// Handles a call to a Python function.
    /// </summary>
    /// <param name="function">The Python function to call (start or update)</param>
    private void PythonCall(Header function)
    {
        if (!this.isSyncConnected)
        {
            this.HandleError("Not connected to a Python script.");
        }

        // Tell Python what function to call
        this.udpClient.Send(new byte[] { (byte)function }, 1);

        // Respond to API calls from Python until we receive a python_finished message
        bool pythonFinished = false;
        while (!pythonFinished)
        {
            // Receive a response from Python
            byte[] data = this.SafeRecieve();
            if (data == null)
            {
                break;
            }
            Header header = (Header)data[0];

            // Send the appropriate response if it was an API call, or break if it was a python_finished message
            byte[] sendData;
            switch (header)
            {
                case Header.error:
                    HandleError("Error code sent from Python.");
                    break;

                case Header.python_finished:
                    pythonFinished = true;
                    break;

                case Header.racecar_get_delta_time:
                    sendData = BitConverter.GetBytes(Time.deltaTime);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.camera_get_color_image:
                    this.SendFragmented(this.racecar.Camera.ColorImageRaw, 32);
                    break;

                case Header.camera_get_depth_image:
                    sendData = this.racecar.Camera.DepthImageRaw;
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.camera_get_width:
                    sendData = BitConverter.GetBytes(CameraModule.ColorWidth);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.camera_get_height:
                    sendData = BitConverter.GetBytes(CameraModule.ColorHeight);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.controller_is_down:
                    Controller.Button buttonDown = (Controller.Button)data[1];
                    sendData = BitConverter.GetBytes(this.racecar.Controller.IsDown(buttonDown));
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.controller_was_pressed:
                    Controller.Button buttonPressed = (Controller.Button)data[1];
                    sendData = BitConverter.GetBytes(this.racecar.Controller.WasPressed(buttonPressed));
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.controller_was_released:
                    Controller.Button buttonReleased = (Controller.Button)data[1];
                    sendData = BitConverter.GetBytes(this.racecar.Controller.WasReleased(buttonReleased));
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.controller_get_trigger:
                    Controller.Trigger trigger = (Controller.Trigger)data[1];
                    sendData = BitConverter.GetBytes(this.racecar.Controller.GetTrigger(trigger));
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.controller_get_joystick:
                    Controller.Joystick joystick = (Controller.Joystick)data[1];
                    Vector2 joystickValues = this.racecar.Controller.GetJoystick(joystick);
                    sendData = new byte[sizeof(float) * 2];
                    Buffer.BlockCopy(new float[] { joystickValues.x, joystickValues.y }, 0, sendData, 0, sendData.Length);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.drive_set_speed_angle:
                    this.racecar.Drive.Speed = BitConverter.ToSingle(data, 4);
                    this.racecar.Drive.Angle = BitConverter.ToSingle(data, 8);
                    break;

                case Header.drive_stop:
                    this.racecar.Drive.Stop();
                    break;

                case Header.drive_set_max_speed:
                    this.racecar.Drive.MaxSpeed = BitConverter.ToSingle(data, 4);
                    break;

                case Header.lidar_get_num_samples:
                    sendData = BitConverter.GetBytes(Lidar.NumSamples);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.lidar_get_samples:
                    sendData = new byte[sizeof(float) * Lidar.NumSamples];
                    Buffer.BlockCopy(this.racecar.Lidar.Samples, 0, sendData, 0, sendData.Length);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.physics_get_linear_acceleration:
                    Vector3 linearAcceleration = this.racecar.Physics.LinearAccceleration;
                    sendData = new byte[sizeof(float) * 3];
                    Buffer.BlockCopy(new float[] { linearAcceleration.x, linearAcceleration.y, linearAcceleration.z }, 0, sendData, 0, sendData.Length);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                case Header.physics_get_angular_velocity:
                    Vector3 angularVelocity = this.racecar.Physics.AngularVelocity;
                    sendData = new byte[sizeof(float) * 3];
                    Buffer.BlockCopy(new float[] { angularVelocity.x, angularVelocity.y, angularVelocity.z }, 0, sendData, 0, sendData.Length);
                    this.udpClient.Send(sendData, sendData.Length);
                    break;

                default:
                    Debug.LogError($">> Error: The function {header} is not supported by RacecarSim.");
                    break;
            }
        }
    }

    /// <summary>
    /// Sends a large amount of data split across several packets.
    /// </summary>
    /// <param name="bytes">The bytes to send (must be divisible by numPackets).</param>
    /// <param name="numPackets">The number of packets to split the data across.</param>
    private void SendFragmented(byte[] bytes, int numPackets)
    {
        int blockSize = bytes.Length / numPackets;
        byte[] sendData = new byte[blockSize];
        for (int i = 0; i < numPackets; i++)
        {
            Buffer.BlockCopy(bytes, i * blockSize, sendData, 0, blockSize);
            this.udpClient.Send(sendData, sendData.Length);

            byte[] response = this.SafeRecieve();
            if (response == null || (Header)response[0] != Header.python_send_next)
            {
                this.HandleError("Unity and Python became out of sync while sending a block message.");
                break;
            }
        }
    }

    /// <summary>
    /// Receives a packet from Python and safely handles UDP exceptions (broken socket, timeout, etc.).
    /// </summary>
    /// <returns>The data in the packet, or null if an error occurred.</returns>
    private byte[] SafeRecieve()
    {
        try
        {
            return this.udpClient.Receive(ref this.pythonEndPoint);
        }
        catch (SocketException e)
        {
            if (e.SocketErrorCode == SocketError.TimedOut)
            {
                this.HandleError("No message received from Python within the alloted time.");
                Debug.LogError(">> Troubleshooting:" +
                    "\n1. Make sure that your Python program does not block or wait. For example, your program should never call time.sleep()." +
                    "\n2. Make sure that your program is not too computationally intensive. Your start and update functions should be able to run in under 10 milliseconds." +
                    "\n3. Make sure that your Python program did not crash or close unexpectedly." +
                    "\n4. Unless you experience an error, do not force-quit your Python application (ctrl+c or ctrl+d).  Instead, end the simulation by pressing the start and back button simultaneously on your Xbox controller (escape and enter on keyboard).");
            }
            else
            {
                this.HandleError("An error occurred when attempting to receive data from Python.");
            }
        }
        return null;
    }

    /// <summary>
    /// Handles when a sync error occurs by showing error text, sending an error response, and returning to default drive.
    /// </summary>
    /// <param name="errorText">The error text to show.</param>
    private void HandleError(string errorText)
    {
        Debug.LogError($">> Error: {errorText} Returning to default drive mode.");
        this.racecar.Hud.SetMessage($"Error: {errorText} Returning to default drive mode.", Color.red, 5, 1);

        if (this.isSyncConnected)
        {
            this.udpClient.Send(new byte[] { (byte)Header.error }, 1);
        }

        this.racecar.EnterDefaultDrive();
    }
    #endregion

    #region Async
    /// <summary>
    /// The UDP client used to handle async API calls from Python.
    /// </summary>
    private UdpClient udpClientAsync;

    /// <summary>
    /// A thread containing a UDP client to process asynchronous API calls from Python.
    /// </summary>
    private Thread asyncClientThread;

    /// <summary>
    /// Creates a UDP client to process asynchronous API calls from Python (for use by Jupyter).
    /// </summary>
    private void ProcessAsyncCalls()
    {
        this.udpClientAsync = new UdpClient(new IPEndPoint(PythonInterface.ipAddress, PythonInterface.unityPortAsync));
        this.udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        while (true)
        {
            IPEndPoint receiveEndPoint = new IPEndPoint(PythonInterface.ipAddress, 0);
            byte[] data = this.udpClientAsync.Receive(ref receiveEndPoint);
            Header header = (Header)data[0];

            byte[] sendData;
            switch (header)
            {
                case Header.connect:
                    if (!this.isSyncConnected)
                    {
                        this.ConnectSyncClient(receiveEndPoint.Port);
                        sendData = new byte[1] { (byte)Header.connect };
                        this.udpClientAsync.Send(sendData, sendData.Length, receiveEndPoint);
                    }
                    break;

                case Header.camera_get_color_image:
                    this.SendFragmentedAsync(this.racecar.Camera.GetColorImageRawAsync(), 32, receiveEndPoint);
                    break;

                case Header.camera_get_depth_image:
                    sendData = this.racecar.Camera.GetDepthImageRawAsync();
                    this.udpClientAsync.Send(sendData, sendData.Length, receiveEndPoint);
                    break;

                case Header.lidar_get_samples:
                    sendData = new byte[sizeof(float) * Lidar.NumSamples];
                    Buffer.BlockCopy(this.racecar.Lidar.Samples, 0, sendData, 0, sendData.Length);
                    this.udpClientAsync.Send(sendData, sendData.Length, receiveEndPoint);
                    break;

                default:
                    Debug.LogError($">> Error: The function {header} is not supported by RacecarSim for async calls.");
                    break;
            }
        }
    }

    /// <summary>
    /// Sends a large amount of data split across several packets via the async client.
    /// </summary>
    /// <param name="bytes">The bytes to send (must be divisible by numPackets).</param>
    /// <param name="numPackets">The number of packets to split the data across.</param>
    private void SendFragmentedAsync(byte[] bytes, int numPackets, IPEndPoint destination)
    {
        int blockSize = bytes.Length / numPackets;
        byte[] sendData = new byte[blockSize];
        for (int i = 0; i < numPackets; i++)
        {
            Buffer.BlockCopy(bytes, i * blockSize, sendData, 0, blockSize);
            this.udpClientAsync.Send(sendData, sendData.Length, destination);

            byte[] response = this.udpClientAsync.Receive(ref destination);
            if (response == null || (Header)response[0] != Header.python_send_next)
            {
                this.udpClientAsync.Send(new byte[] { (byte)Header.error }, 1, destination);
                break;
            }
        }
    }
    #endregion
}
