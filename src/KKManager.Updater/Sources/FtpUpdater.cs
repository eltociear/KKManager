using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP;
using FluentFTP.Helpers;
using FluentFTP.Proxy;
using KKManager.Updater.Data;
using KKManager.Util;

namespace KKManager.Updater.Sources
{
    public class FtpUpdater : UpdateSourceBase
    {
        private readonly Dictionary<FtpClient, bool> _clientCache = new Dictionary<FtpClient, bool>();

        private FtpClient GetUnusedClient()
        {
            lock (_clientCache)
            {
                var cl = _clientCache.FirstOrDefault(x => !x.Value);
                if (cl.Key == null)
                {
                    var newClient = CreateNewClient();
                    _clientCache.Add(newClient, true);
                    return newClient;
                }

                _clientCache[cl.Key] = true;
                return cl.Key;
            }
        }

        private void ReturnClient(FtpClient cl)
        {
            if (cl == null) return;
            lock (_clientCache)
                _clientCache[cl] = false;
        }

        private readonly Uri _serverUri;
        private readonly NetworkCredential _networkCredentials;
        private FtpListItem[] _allNodes;

        public FtpUpdater(Uri serverUri, int discoveryPriority, int downloadPriority = 1, NetworkCredential credentials = null) : base(serverUri.Host, discoveryPriority, downloadPriority)
        {
            if (serverUri == null) throw new ArgumentNullException(nameof(serverUri));
            _serverUri = serverUri;

            if (credentials == null)
            {
                var info = serverUri.UserInfo.Split(new[] { ':' }, 2, StringSplitOptions.None);
                if (info.Length == 2)
                    _networkCredentials = new NetworkCredential(info[0], info[1]);
            }
            else
                _networkCredentials = credentials;

            //FtpTrace.LogFunctions = false;
            FtpTrace.EnableTracing = false;

            _clientCache.Add(CreateNewClient(), false);
        }

        private FtpClient CreateNewClient()
        {
            Console.WriteLine("Creating a new ftp client");
            var serverUri = _serverUri;
            var credentials = _networkCredentials;
            FtpClient client;
            if (!System.Net.WebRequest.DefaultWebProxy.IsBypassed(serverUri))
            {
                var proxy = System.Net.WebRequest.DefaultWebProxy.GetProxy(serverUri);
                client = new FtpClientHttp11Proxy(new ProxyInfo { Host = proxy.Host, Port = proxy.Port });
                client.Host = serverUri.Host;
                client.Credentials = credentials;
                if (!serverUri.IsDefaultPort)
                {
                    client.Port = serverUri.Port;
                }
            }
            else
            {
                if (serverUri.IsDefaultPort)
                    client = new FtpClient(serverUri.Host, credentials);
                else
                    client = new FtpClient(serverUri.Host, serverUri.Port, credentials);
            }

            client.EncryptionMode = FtpEncryptionMode.Explicit;
            client.DataConnectionEncryption = true;
            // Retrying is handled higher up the tree
            client.RetryAttempts = 1;
            return client;
        }

        public override void Dispose()
        {
            lock (_clientCache)
            {
                foreach (var cl in _clientCache.Keys)
                    cl.Dispose();
                _clientCache.Clear();
            }
        }

        public override async Task<List<UpdateTask>> GetUpdateItems(CancellationToken cancellationToken)
        {
            var cl = GetUnusedClient();
            try
            {
                await Connect(cl, cancellationToken);
                _allNodes = await cl.GetListingAsync("/", FtpListOption.Recursive | FtpListOption.Size, cancellationToken);
                return await base.GetUpdateItems(cancellationToken);
            }
            finally
            {
                ReturnClient(cl);
            }
        }

        protected override async Task<Stream> DownloadFileAsync(string updateFileName, CancellationToken cancellationToken)
        {
            var item = GetRemoteItem(updateFileName);
            if (item == null)
                throw new FileNotFoundException("File doesn't exist on host");

            cancellationToken.ThrowIfCancellationRequested();

            var cl = GetUnusedClient();
            try
            {
                await Connect(cl, cancellationToken);

                var str = new MemoryStream();
                if (await cl.DownloadAsync(str, updateFileName, 0, null, cancellationToken))
                {
                    str.Seek(0, SeekOrigin.Begin);
                    return str;
                }
                // Cleanup if download fails
                str.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("Failed to download file");
            }
            finally
            {
                ReturnClient(cl);
            }
        }

        protected override IRemoteItem GetRemoteRootItem(string serverPath)
        {
            if (serverPath == null) throw new ArgumentNullException(nameof(serverPath));
            FtpListItem remote = GetRemoteItem(serverPath);
            if (remote == null) return null;
            var remoteItem = new FtpRemoteItem(remote, this, remote.FullName);
            return remoteItem;
        }

        private FtpListItem GetRemoteItem(string serverPath)
        {
            return _allNodes.FirstOrDefault(item => PathTools.PathsEqual(item.FullName, serverPath));
        }

        private static readonly HttpClient _httpClient = new HttpClient();

        private async Task Connect(FtpClient _client, CancellationToken cancellationToken)
        {
            if (!_client.IsConnected)
            {
                // Need to wrap the connect into a new task because it can block main thread when failing to connect
                await Task.Run(async () =>
                {
                    var t = _client.AutoConnectAsync(cancellationToken);
                    try
                    {
                        var str = await _httpClient.GetStringAsync(new Uri("https://" + Origin + "/usage", UriKind.Absolute));
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            Usage = Math.Min(100, Math.Max(0, (int)Math.Round(double.Parse(str))));
                            MaxConcurrentDownloads = Math.Min(5, Math.Max(1, 10 - Usage / 10));
                            Console.WriteLine($"Server usage for {Origin}: {Usage}%  MaxConcurrentDownloads={MaxConcurrentDownloads}");
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"Could not get usage statistics for {Origin}: {e}");
                        MaxConcurrentDownloads = 1;
                    }
                    await t;
                }, cancellationToken);

                // todo hack, some servers don't announce the capability, needed for proper functionality
                _client.RecursiveList = true;
            }
        }

        private static DateTime GetDate(FtpListItem ftpListItem)
        {
            if (ftpListItem == null) throw new ArgumentNullException(nameof(ftpListItem));
            return ftpListItem.Modified != DateTime.MinValue ? ftpListItem.Modified : ftpListItem.Created;
        }

        private IEnumerable<FtpListItem> GetSubNodes(FtpListItem remoteDir)
        {
            if (remoteDir == null) throw new ArgumentNullException(nameof(remoteDir));
            if (remoteDir.Type != FtpFileSystemObjectType.Directory) throw new ArgumentException("remoteDir has to be a directory");

            var remoteDirName = PathTools.NormalizePath(remoteDir.FullName) + "/";
            var remoteDirDepth = remoteDirName.Count(c => c == '/' || c == '\\');

            return _allNodes.Where(
                item =>
                {
                    if (item == remoteDir) return false;
                    var itemFilename = PathTools.NormalizePath(item.FullName);
                    // Make sure it's inside the directory and not inside one of the subdirectories
                    return itemFilename.StartsWith(remoteDirName, StringComparison.OrdinalIgnoreCase) &&
                           itemFilename.Count(c => c == '/' || c == '\\') == remoteDirDepth;
                });
        }

        private async Task UpdateItem(FtpListItem sourceItem, FileInfo targetPath, IProgress<double> progressCallback, CancellationToken cancellationToken)
        {
            // Delete old file if any exists so the download doesn't try to append to it. Append mode is needed for retrying downloads to resume instead of restarting
            targetPath.Delete();

            var cl = GetUnusedClient();
            try
            {
                await Connect(cl, cancellationToken);

                await cl.DownloadFileAsync(
                    targetPath.FullName, sourceItem.FullName,
                    FtpLocalExists.Resume, FtpVerify.Retry | FtpVerify.Delete | FtpVerify.Throw,
                    new Progress<FtpProgress>(progress => progressCallback.Report(progress.Progress)),
                    cancellationToken);
            }
            finally
            {
                ReturnClient(cl);
            }
        }

        private sealed class FtpRemoteItem : IRemoteItem
        {
            private readonly string _rootFolder;

            public FtpRemoteItem(FtpListItem sourceItem, FtpUpdater source, string rootFolder)
            {
                if (sourceItem == null) throw new ArgumentNullException(nameof(sourceItem));
                if (source == null) throw new ArgumentNullException(nameof(source));

                if (rootFolder != null)
                {
                    _rootFolder = rootFolder;
                    if (!sourceItem.FullName.StartsWith(_rootFolder)) throw new IOException($"Remote item full path {sourceItem.FullName} doesn't start with the specified root path {_rootFolder}");
                    ClientRelativeFileName = sourceItem.FullName.Substring(_rootFolder.Length);
                }

                SourceItem = sourceItem;
                Source = source;
                ItemSize = SourceItem.Size;
                ModifiedTime = GetDate(SourceItem);
            }

            public string Name => SourceItem.Name;
            public long ItemSize { get; }
            public DateTime ModifiedTime { get; }
            public bool IsDirectory => SourceItem.Type == FtpFileSystemObjectType.Directory;
            public bool IsFile => SourceItem.Type == FtpFileSystemObjectType.File;
            public string ClientRelativeFileName { get; }

            public FtpUpdater Source { get; }
            public FtpListItem SourceItem { get; }
            UpdateSourceBase IRemoteItem.Source => Source;

            public IRemoteItem[] GetDirectoryContents(CancellationToken cancellationToken)
            {
                return Source.GetSubNodes(SourceItem).Select(x => (IRemoteItem)new FtpRemoteItem(x, Source, _rootFolder)).ToArray();
            }

            public async Task Download(FileInfo downloadTarget, Progress<double> progressCallback, CancellationToken cancellationToken)
            {
                await Source.UpdateItem(SourceItem, downloadTarget, progressCallback, cancellationToken);
            }
        }
    }
}
