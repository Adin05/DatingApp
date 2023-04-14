using API.DTOs;
using API.Entities;
using API.Extensions;
using API.Interfaces;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace API.SignalR
{
    [Authorize]
    public class MessageHub : Hub
    {
        private readonly IMapper _mapper;
        private readonly IHubContext<PresenceHub> _presenceHub;
        private readonly IUnitOfWork _ouw;
        public MessageHub( IMapper mapper, IHubContext<PresenceHub> presenceHub, IUnitOfWork ouw)
        {
            _ouw = ouw;
            _presenceHub = presenceHub;
            _mapper = mapper;
        }
        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();
            var otherUsers = httpContext.Request.Query["user"];
            var groupName = GetGroupName(Context.User.GetUsername(), otherUsers);
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            var group = await AddToGroup(groupName);

            await Clients.Group(groupName).SendAsync("UpdatedGroup", group);

            var messages = await _ouw.MessageRepository
                .GetMessageThread(Context.User.GetUsername(), otherUsers);
            
            if(_ouw.HasChanges()) await _ouw.Complete();

            await Clients.Caller.SendAsync("ReceiveMessageThread", messages);
        }
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var group = await RemoveFromMessageGroup();
            await Clients.Group(group.Name).SendAsync("UpdatedGroup", group);
            await base.OnDisconnectedAsync(exception);
        }
        private string GetGroupName(string caller, string other)
        {
            var stringCompare = string.CompareOrdinal(caller, other) < 0;
            return stringCompare ? $"{caller}-{other}" : $"{other}-{caller}";
        }
        public async Task SendMessage(CreateMessageDto createMessageDto)
        {
            var username = Context.User.GetUsername();

            if (username == createMessageDto.RecipientUsername.ToLower())
                throw new HubException("You cannot send messages to yourself.");

            var sender = await _ouw.UserRepository.GetUserByNameAsync(username);
            var recipient = await _ouw.UserRepository.GetUserByNameAsync(createMessageDto.RecipientUsername);

            if (recipient == null) throw new HubException("Not found user");

            var message = new Message
            {
                Sender = sender,
                Recipient = recipient,
                SenderUsername = sender.UserName,
                RecipientUsername = recipient.UserName,
                Content = createMessageDto.Content,
            };

            var groupName = GetGroupName(sender.UserName, recipient.UserName);

            var group = await _ouw.MessageRepository.GetMessageGroup(groupName);

            if (group.Connections.Any(m => m.Username == recipient.UserName))
            {
                message.DateRead = DateTime.UtcNow;
            }
            else
            {
                var connections = await PresenceTracker.GetConnectionsForUser(recipient.UserName);
                if (connections != null)
                {
                    await _presenceHub.Clients.Clients(connections)
                    .SendAsync("NewMessageReceived", new { username = sender.UserName, knownAs = sender.KnownAs });
                }
            }

            _ouw.MessageRepository.AddMessage(message);

            if (await _ouw.Complete())
            {
                await Clients.Group(groupName).SendAsync("NewMessage", _mapper.Map<MessageDto>(message));
            }
        }

        private async Task<Group> AddToGroup(string groupName)
        {
            var group = await _ouw.MessageRepository.GetMessageGroup(groupName);
            var connection = new Connection(Context.ConnectionId, Context.User.GetUsername());

            if (group == null)
            {
                group = new Group(groupName);
                _ouw.MessageRepository.AddGroup(group);
            }

            group.Connections.Add(connection);
            if (await _ouw.Complete()) return group;
            throw new HubException("Fail to add to group");
        }
        private async Task<Group> RemoveFromMessageGroup()
        {
            var group = await _ouw.MessageRepository.GetGroupForConnection(Context.ConnectionId);
            var connection = group.Connections.FirstOrDefault(m => m.ConnectionId == Context.ConnectionId);
            _ouw.MessageRepository.RemoveConnection(connection);
            
            if (await _ouw.Complete()) return group;
            throw new HubException("Fail to remove from group");
        }
    }
}