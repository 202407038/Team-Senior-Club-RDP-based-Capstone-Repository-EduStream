using System.Drawing;
using EduStream.ShareViewer;
using Forms = System.Windows.Forms;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class ProfessorViewerReleaseTests
{
    [Fact]
    public void Watch_WhenConnectThrows_ReleasesViewerAndAllowsSameStudentRetry()
    {
        using var reception = new ProfessorReception();
        var broken = new ConnectTestPanel { FailConnect = true };
        var original = Assert.Throws<InvalidOperationException>(() =>
            reception.Watch("student", broken, "invite", "professor", "password"));
        Assert.Equal("연결 실패", original.Message);
        Assert.True(broken.IsDisposed);

        var retry = new ConnectTestPanel();
        reception.Watch("student", retry, "invite", "professor", "password");
        Assert.Equal(1, retry.ConnectCalls);
        reception.Release("student");
        Assert.True(retry.IsDisposed);
    }

    [Fact]
    public void Watch_WhenConnectAndCleanupFail_PreservesBothErrorsAndAllowsReleaseRetry()
    {
        using var reception = new ProfessorReception();
        var broken = new ConnectTestPanel { FailConnect = true, FailDisposeOnce = true };
        var error = Assert.Throws<AggregateException>(() =>
            reception.Watch("student", broken, "invite", "professor", "password"));
        Assert.Collection(error.InnerExceptions,
            ex => Assert.Equal("연결 실패", ex.Message),
            ex => Assert.Equal("해제 실패", ex.Message));
        Assert.False(broken.IsDisposed);
        reception.Release("student");
        Assert.True(broken.IsDisposed);
        var retry = new ConnectTestPanel();
        reception.Watch("student", retry, "invite", "professor", "password");
        Assert.Equal(1, retry.ConnectCalls);
    }

    [Fact]
    public void Watch_WhenAnotherStudentFails_PreservesExistingViewer()
    {
        using var reception = new ProfessorReception();
        var first = new ConnectTestPanel();
        reception.Watch("first", first, "invite", "professor", "password");
        var broken = new ConnectTestPanel { FailConnect = true };
        Assert.Throws<InvalidOperationException>(() =>
            reception.Watch("second", broken, "invite", "professor", "password"));
        Assert.False(first.IsDisposed);
        Assert.True(broken.IsDisposed);
        reception.Release("first");
        Assert.True(first.IsDisposed);
    }

    public sealed class ConnectTestPanel : Forms.Panel
    {
        public bool FailConnect { get; init; }
        public bool FailDisposeOnce { get; set; }
        public int ConnectCalls { get; private set; }
        public void Connect(string connectionString, string name, string password)
        {
            ConnectCalls++;
            if (FailConnect) throw new InvalidOperationException("연결 실패");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && FailDisposeOnce)
            {
                FailDisposeOnce = false;
                throw new InvalidOperationException("해제 실패");
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Release_FromAnotherThread_RemovesParentAndDisposes()
    {
        Forms.Form? form = null;
        Forms.Panel? panel = null;
        using var ready = new ManualResetEventSlim(false);
        var ui = new Thread(() =>
        {
            form = new Forms.Form { ShowInTaskbar = false, Width = 320, Height = 240 };
            panel = new Forms.Panel { Size = new Size(120, 80) };
            form.Controls.Add(panel);
            form.Show();
            panel.CreateControl();
            ready.Set();
            Forms.Application.Run();
        })
        { IsBackground = true, Name = "EduStream-ViewerRelease-STA" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var connection = new ProfessorViewerConnection(panel!);
            connection.ReleaseAfterSharingStopped();
            Assert.True(panel!.IsDisposed);
        }
        finally
        {
            form!.BeginInvoke(new Action(Forms.Application.ExitThread));
            Assert.True(ui.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void Release_WhenDisposeFails_StaysIncompleteUntilRetrySucceeds()
    {
        var panel = new FailOncePanel();
        var connection = new ProfessorViewerConnection(panel);

        var ex = Assert.Throws<InvalidOperationException>(() => connection.ReleaseAfterSharingStopped());
        Assert.Contains("해제 실패", ex.Message);
        Assert.False(panel.IsDisposed);

        connection.ReleaseAfterSharingStopped();
        Assert.True(panel.IsDisposed);
    }

    private sealed class FailOncePanel : Forms.Panel
    {
        private bool _fail = true;

        protected override void Dispose(bool disposing)
        {
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("해제 실패");
            }

            base.Dispose(disposing);
        }
    }
}
