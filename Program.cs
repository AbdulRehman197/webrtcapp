﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An example WebRTC server application that attempts to establish
// a WebRTC data channel with a remote peer.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 12 Sep 2020	Aaron Clauson	Created, Dublin, Ireland.
// 09 Apr 2021  Aaron Clauson   Updated for new SCTP stack and added crude load
//                              test capability.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using Newtonsoft.Json;
using WebSocketSharp;
using TinyJson;
using System.Linq.Expressions;
namespace demo
{
    class Program
    {

        private const int WEBSOCKET_PORT = 8081;
        private const string STUN_URL = "stun:stun.l.google.com:19302";
        private const int JAVASCRIPT_SHA256_MAX_IN_SIZE = 65535;
        private const int SHA256_OUTPUT_SIZE = 32;
        private const int MAX_LOADTEST_COUNT = 100;

        private static Microsoft.Extensions.Logging.ILogger logger = NullLogger.Instance;

        private static uint _loadTestPayloadSize = 0;
        private static int _loadTestCount = 0;
        private const string ANSWERSDP = "";

        async static Task Main()
        {

            Console.WriteLine("WebRTC No Signalling Server Sample Program");
            Console.WriteLine("Press ctrl-c to exit.");

            // Plumbing code to facilitate a graceful exit.
            CancellationTokenSource exitCts = new CancellationTokenSource(); // Cancellation token to stop the SIP transport and RTP stream.
            ManualResetEvent exitMre = new ManualResetEvent(false);

            logger = AddConsoleLogger();

            // Ctrl-c will gracefully exit the call at any point.
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exitMre.Set();
            };

            using WebSocket ws = new WebSocket("ws://road-brief-ellipse.glitch.me/");
             ws.Connect();
      
               var pc = CreatePeerConnection();
            var offerSdp = pc.createOffer(null);
            await pc.setLocalDescription(offerSdp);

            var offerSerialised = Newtonsoft.Json.JsonConvert.SerializeObject(offerSdp,
            new Newtonsoft.Json.Converters.StringEnumConverter());
            var offerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(offerSerialised));
            ws.Send(offerBase64);
            Console.WriteLine(offerBase64);
            
            ws.OnMessage += (sender, e) =>
            {
                Console.WriteLine("Laputa says: " + Encoding.UTF8.GetString(e.RawData));
                var ANSWERSDP = Encoding.UTF8.GetString(e.RawData);
                string remoteAnswer = Encoding.UTF8.GetString(Convert.FromBase64String(ANSWERSDP));
                RTCSessionDescriptionInit answerInit = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(remoteAnswer);
                pc.setRemoteDescription(answerInit);
            };
            exitMre.WaitOne();
            Task.Delay(1000).Wait();
        }

        private static RTCPeerConnection CreatePeerConnection()
        {
            RTCConfiguration config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer> { new RTCIceServer { urls = STUN_URL } }
            };
            var pc = new RTCPeerConnection(config);
            pc.ondatachannel += (rdc) =>
            {
                rdc.onopen += () => logger.LogDebug($"Data channel {rdc.label} opened.");
                rdc.onclose += () => logger.LogDebug($"Data channel {rdc.label} closed.");
                rdc.onmessage += (datachan, type, data) =>
                {
                    switch (type)
                    {
                        case DataChannelPayloadProtocols.WebRTC_Binary_Empty:
                        case DataChannelPayloadProtocols.WebRTC_String_Empty:
                            logger.LogInformation($"Data channel {datachan.label} empty message type {type}.");
                            break;

                        case DataChannelPayloadProtocols.WebRTC_Binary:
                            string jsSha256 = DoJavscriptSHA256(data);
                            logger.LogInformation($"Data channel {datachan.label} received {data.Length} bytes, js mirror sha256 {jsSha256}.");
                            rdc.send(jsSha256);

                            if (_loadTestCount > 0)
                            {
                                DoLoadTestIteration(rdc, _loadTestPayloadSize);
                                _loadTestCount--;
                            }

                            break;

                        case DataChannelPayloadProtocols.WebRTC_String:
                            var msg = Encoding.UTF8.GetString(data);
                            logger.LogInformation($"Data channel {datachan.label} message {type} received: {msg}.");
                           
                            var loadTestMatch = Regex.Match(msg, @"^\s*(?<sendSize>\d+)\s*x\s*(?<testCount>\d+)");

                            if (loadTestMatch.Success)
                            {
                                uint sendSize = uint.Parse(loadTestMatch.Result("${sendSize}"));
                                _loadTestCount = int.Parse(loadTestMatch.Result("${testCount}"));
                                _loadTestCount = (_loadTestCount <= 0 || _loadTestCount > MAX_LOADTEST_COUNT) ? MAX_LOADTEST_COUNT : _loadTestCount;
                                _loadTestPayloadSize = (sendSize > pc.sctp.maxMessageSize) ? pc.sctp.maxMessageSize : sendSize;

                                logger.LogInformation($"Starting data channel binary load test, payload size {sendSize}, test count {_loadTestCount}.");
                                DoLoadTestIteration(rdc, _loadTestPayloadSize);
                                _loadTestCount--;
                            }

                            else
                            {
                                /*   // Do a string echo.
                                   rdc.send($"replay from DotNet: {msg}");*/
                                string replaystring = null;
                                while (string.IsNullOrWhiteSpace(replaystring))
                                {
                                    Console.Write("=> ");
                                    replaystring = Console.ReadLine();
                                    rdc.send($"replay from DotNet: {replaystring}");
                                }
                            }
                            break;
                    }
                };
            };

            var dc =  pc.createDataChannel("test", null);
       
            pc.onconnectionstatechange += (state) =>
            {
                logger.LogDebug($"Peer connection state change to {state}.");

                if (state == RTCPeerConnectionState.failed)
                {
                    pc.Close("ice disconnection");
                }
            };

            // Diagnostics.
            //pc.OnReceiveReport += (re, media, rr) => logger.LogDebug($"RTCP Receive for {media} from {re}\n{rr.GetDebugSummary()}");
            //pc.OnSendReport += (media, sr) => logger.LogDebug($"RTCP Send for {media}\n{sr.GetDebugSummary()}");
            //pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) => logger.LogDebug($"STUN {msg.Header.MessageType} received from {ep}.");
            pc.oniceconnectionstatechange += (state) => logger.LogDebug($"ICE connection state change to {state}.");
            pc.onsignalingstatechange += () => logger.LogDebug($"Signalling state changed to {pc.signalingState}.");

            return pc;
        }

        private static void DoLoadTestIteration(RTCDataChannel dc, uint payloadSize)
        {
            var rndBuffer = new byte[payloadSize];
            Crypto.GetRandomBytes(rndBuffer);
            logger.LogInformation($"Data channel sending {payloadSize} random bytes, hash {DoJavscriptSHA256(rndBuffer)}.");
            dc.send(rndBuffer);
        }

        /// <summary>
        /// The Javascript hash function only allows a maximum input of 65535 bytes. In order to hash
        /// larger buffers for testing purposes the buffer is split into 65535 slices and then the hashes
        /// of each of the slices hashed.
        /// </summary>
        /// <param name="buffer">The buffer to perform the Javascript SHA256 hash of hashes on.</param>
        /// <returns>A hex string of the resultant hash.</returns>
        private static string DoJavscriptSHA256(byte[] buffer)
        {
            int iters = (buffer.Length <= JAVASCRIPT_SHA256_MAX_IN_SIZE) ? 1 : buffer.Length / JAVASCRIPT_SHA256_MAX_IN_SIZE;
            iters += (buffer.Length > iters * JAVASCRIPT_SHA256_MAX_IN_SIZE) ? 1 : 0;

            byte[] hashOfHashes = new byte[iters * SHA256_OUTPUT_SIZE];

            for (int i = 0; i < iters; i++)
            {
                int startPosn = i * JAVASCRIPT_SHA256_MAX_IN_SIZE;
                int length = JAVASCRIPT_SHA256_MAX_IN_SIZE;
                length = (startPosn + length > buffer.Length) ? buffer.Length - startPosn : length;

                var slice = new ArraySegment<byte>(buffer, startPosn, length);

                using (var sha256 = SHA256.Create())
                {
                    Buffer.BlockCopy(sha256.ComputeHash(slice.ToArray()), 0, hashOfHashes, i * SHA256_OUTPUT_SIZE, SHA256_OUTPUT_SIZE);
                }
            }

            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(hashOfHashes).HexStr();
            }
        }

        /// <summary>
        /// Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        private static Microsoft.Extensions.Logging.ILogger AddConsoleLogger()
        {
            var seriLogger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            var factory = new SerilogLoggerFactory(seriLogger);
            SIPSorcery.LogFactory.Set(factory);
            return factory.CreateLogger<Program>();
        }
    }
}
