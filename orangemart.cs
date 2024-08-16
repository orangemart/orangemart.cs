using Newtonsoft.Json;
using UnityEngine;
using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Libraries.Covalence;
namespace Oxide.Plugins
{
    [Info("Plugin Name", "Author/s", "0.0.1")]
    [Description("One sentence plugin description.")]
    class PluginName : CovalencePlugin
    {
        private void OnServerInitialized()
        {
            AddCovalenceCommand("ping", "PingPong");
        }
        private void PingPong(IPlayer player, string command, string[] args)
        {
            player.Reply("Pong");
        }
    }
}
