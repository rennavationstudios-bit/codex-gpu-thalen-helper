namespace ThalenHelper.Core;

internal sealed class ModelStorageOperationLease : IDisposable
{
    private const string MutexName = @"Local\CodexGpuThalenHelperModelStorage";
    private readonly TaskCompletionSource<bool> _release;
    private readonly Task _ownerTask;
    private bool _released;

    private ModelStorageOperationLease(TaskCompletionSource<bool> release, Task ownerTask)
    {
        _release = release;
        _ownerTask = ownerTask;
    }

    public static async Task<ModelStorageOperationLease> AcquireAsync(CancellationToken cancellationToken)
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerTask = Task.Run(() =>
        {
            using var mutex = new Mutex(false, MutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    var index = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]);
                    if (index == 1)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    ownsMutex = true;
                }
                catch (AbandonedMutexException)
                {
                    // Ownership transfers to this process when the previous owner exited.
                    ownsMutex = true;
                }

                cancellationToken.ThrowIfCancellationRequested();
                acquired.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                acquired.TrySetException(exception);
            }
            finally
            {
                if (ownsMutex)
                {
                    mutex.ReleaseMutex();
                }
            }
        }, CancellationToken.None);

        await acquired.Task.ConfigureAwait(false);
        return new ModelStorageOperationLease(release, ownerTask);
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _release.TrySetResult(true);
        _ownerTask.GetAwaiter().GetResult();
    }
}
