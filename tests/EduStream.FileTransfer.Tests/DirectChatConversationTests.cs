using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Core.Common;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;

namespace EduStream.FileTransfer.Tests;

public sealed class DirectChatConversationTests
{
    [Fact]
    public void ConversationsKeepMessagesAndUnreadCountsSeparate()
    {
        var publicMessages = new ObservableCollection<ChatLine>();
        var book = new ChatConversations(publicMessages) { IsReadingActive = true };
        var alice = book.GetOrAdd("connection-A", "학생 A");
        var bob = book.GetOrAdd("connection-B", "학생 B");
        alice.Messages.Add(ChatLine.User("학생 A", "질문"));
        bob.Messages.Add(ChatLine.User("학생 B", "질문"));
        publicMessages.Add(ChatLine.User("학생 C", "전체"));
        Assert.Equal(2, book.UnreadCount);
        Assert.Single(publicMessages);
        book.Selected = alice;
        Assert.Equal(0, alice.UnreadCount);
        Assert.Equal(1, bob.UnreadCount);
        book.IsReadingActive = false;
        alice.Messages.Add(ChatLine.User("학생 A", "추가"));
        Assert.Equal(1, alice.UnreadCount);
        alice.Messages.Add(ChatLine.User("교수자", "답변", isSelf: true));
        Assert.Equal(1, alice.UnreadCount);
        alice.IsAvailable = false;
        Assert.Same(alice, book.Selected); // 대상 이탈 때 전체 전송으로 전환하면 안 된다.
        Assert.Contains("연결 종료", alice.Label);
    }

    [Fact]
    public void ExplicitReadOnlyClearsVisibleConversationWhileActive()
    {
        var book = new ChatConversations([]);
        var alice = book.GetOrAdd("a", "학생 A");
        var bob = book.GetOrAdd("b", "학생 B");
        book.Selected = alice;
        alice.Messages.Add(ChatLine.User("학생 A", "개인 질문"));
        bob.Messages.Add(ChatLine.User("학생 B", "다른 질문"));
        book.MarkSelectedRead();
        Assert.Equal(2, book.UnreadCount);
        book.IsReadingActive = true; // 창 활성화만으로 과거 메시지를 읽었다고 가정하지 않는다.
        Assert.Equal(2, book.UnreadCount);
        book.MarkSelectedRead();
        Assert.Equal(0, alice.UnreadCount);
        Assert.Equal(1, bob.UnreadCount);
    }

    [Fact]
    public void NewSessionDropsPrivateConversationsButSameSessionKeepsThem()
    {
        var book = new ChatConversations([]);
        var session = Guid.NewGuid(); book.SetSession(session);
        book.GetOrAdd("professor", "교수자").Messages.Add(ChatLine.User("교수자", "개인"));
        book.SetSession(session);
        Assert.Equal(2, book.Items.Count);
        book.SetSession(Guid.NewGuid());
        Assert.Single(book.Items);
        Assert.Equal("", book.Selected.Key);
    }

    [Fact]
    public void CapabilityAndDedicatedPacketNeverPretendToBePublicChat()
    {
        var session = new SessionClient(new InMemoryLogSink());
        Assert.True(session.CreateJoinRequest("127.0.0.1", 5000, "학생 A").SupportsDirectChat);
        var serializer = new PacketSerializer();
        Assert.False(serializer.Deserialize<AckPacket>(Encoding.UTF8.GetBytes("{\"AckCode\":\"SESSION_JOINED\"}"))!.SupportsDirectChat);
        var packet = new DirectChatPacket { Message = "개인", StudentConnectionId = Guid.NewGuid() };
        var copy = serializer.Deserialize<DirectChatPacket>(serializer.Serialize(packet))!;
        Assert.Equal(PacketType.DirectChat, copy.MessageType);
        Assert.Equal(packet.StudentConnectionId, copy.StudentConnectionId);
    }

    [Fact]
    public async Task ClientRejectsStaleSessionOrWrongRecipientAndDoesNotAddPrivateToPublic()
    {
        var vm = new ClientViewModel();
        try
        {
            var session = Guid.NewGuid(); var connection = Guid.NewGuid();
            var sessionClient = (SessionClient)typeof(ClientViewModel).GetField("_sessionClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            await sessionClient.ApplyJoinAckAsync(new AckPacket { SessionId = session }, "127.0.0.1", 5000);
            typeof(ClientViewModel).GetProperty("IsConnected")!.SetValue(vm, true);
            typeof(ClientViewModel).GetField("_supportsDirectChat", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
            typeof(ClientViewModel).GetField("_chatConnectionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, connection);
            void Receive(Guid s, Guid c) => typeof(ClientViewModel).GetMethod("HandleDirectChat", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [new DirectChatPacket { SessionId = s, StudentConnectionId = c, FromProfessor = true, Message = "질문 답변" }]);
            Receive(Guid.NewGuid(), connection);
            Receive(session, Guid.NewGuid());
            Assert.Single(vm.Conversations.Items);
            Receive(session, connection);
            Assert.Empty(vm.ChatMessages);
            Assert.Single(vm.Conversations.GetOrAdd("professor", "교수자").Messages);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
