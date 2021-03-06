﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Bayeux;
using Dotbot.Diagnostics;
using Dotbot.Gitter.Models;
using Dotbot.Models;
using Dotbot.Models.Events;
using Newtonsoft.Json.Linq;

namespace Dotbot.Gitter
{
    internal sealed class GitterAdapter : IAdapter, IWorker
    {
        private readonly GitterBroker _broker;
        private readonly BayeuxClient _bayeux;
        private readonly IEventQueue _eventQueue;
        private readonly ILog _log;

        public string FriendlyName => "Gitter adapter";
        public IBroker Broker => _broker;

        public GitterAdapter(GitterBroker broker, GitterConfiguration configuration, IEventQueue eventQueue, ILog log)
        {
            _broker = broker;
            _eventQueue = eventQueue;
            _log = new LogNameDecorator("Gitter", log);

            // Create the Bayeux client.
            var settings = new BayeuxClientSettings(new Uri("https://ws.gitter.im/faye"));
            settings.Extensions.Add(new GitterTokenExtension(configuration.Token));
            _bayeux = new BayeuxClient(settings);
        }

        public async Task<bool> Run(CancellationToken token)
        {
            try
            {
                // Connect to the Gitter Faye endpoint.
                _bayeux.Connect();

                // Get the current user (the bot).
                var user = await _broker.GetCurrentUser();
                _log.Information("Current user is {0}.", user.Username);

                // Subscribe to rooms.
                var rooms = await _broker.GetRooms();
                foreach (var room in rooms)
                {
                    _bayeux.Subscribe($"/api/v1/rooms/{room.Id}/chatMessages", message => { MessageReceived(user, room, message); });
                    _log.Information("Subscribed to {0} ({1}).", room.Name, room.Id);
                }

                // Subscribe to room events for the current user.
                _bayeux.Subscribe($"/api/v1/user/{user.Id}/rooms", raw =>
                {
                    var envelope = ((JObject)raw.Data).ToObject<Envelope<GitterRoom>>();
                    if (envelope.Operation == null || !envelope.Operation.Equals("create", StringComparison.OrdinalIgnoreCase))
                    {
                    // Subscribe to messages in this channel.
                    var room = envelope.Model.CreateRoom();
                        _bayeux.Subscribe($"/api/v1/rooms/{envelope.Model.Id}/chatMessages", message => { MessageReceived(user, room, message); });
                        _log.Information("Subscribed to {0} ({1}).", room.Name, room.Id);
                    }
                });

                // Wait for disconnect.
                token.WaitHandle.WaitOne(Timeout.Infinite);

                // Don't stop the application.
                return true;
            }
            finally
            {
                _bayeux.Disconnect();
            }
        }

        private void MessageReceived(User bot, Room room, IBayeuxMessage raw)
        {
            // Get the Faya envelope object.
            var envelope = ((JObject)raw.Data).ToObject<Envelope<GitterMessage>>();
            if (envelope.Operation == null || !envelope.Operation.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // From ourselves?
            var from = envelope.Model.FromUser;
            if (from?.Id == bot.Id)
            {
                return;
            }

            // Create the message representation.
            var fromUser = new User(from?.Id, from?.Username, from?.DisplayName);
            var message = new Message(fromUser, envelope.Model.Text);

            // Enqueue the message.
            _eventQueue.Enqueue(new MessageEvent(bot, room, message, _broker));
        }
    }
}
