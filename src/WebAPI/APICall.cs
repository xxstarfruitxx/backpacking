﻿using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using StableSwarmUI.Accounts;
using System.Net.WebSockets;

namespace StableSwarmUI.WebAPI;


/// <summary>Represents an API Call route and associated core data (permissions, etc).</summary>
/// <param name="Name">The name, ie the call path, in full.</param>
/// <param name="Call">Actual call function: an async function that takes the HttpContext and the JSON input, and returns JSON output.</param>
/// <param name="IsWebSocket">Whether this call is for websockets. If false, normal HTTP API.</param>
public record class APICall(string Name, Func<HttpContext, Session, WebSocket, JObject, Task<JObject>> Call, bool IsWebSocket)
{
    // TODO: Permissions, etc.
}
