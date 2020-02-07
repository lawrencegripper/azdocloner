using System;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System.Collections.Generic;
using System.Linq;
using LibGit2Sharp;
using System.IO;
using LibGit2Sharp.Handlers;

namespace azdocloner
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Uri org1Url = new Uri("https://dev.azure.com/" + Environment.GetEnvironmentVariable("ORG1"));
            String project1 = Environment.GetEnvironmentVariable("PROJ1");
            String pat1 = Environment.GetEnvironmentVariable("PAT1");  // See https://docs.microsoft.com/azure/devops/integrate/get-started/authentication/pats

            Uri org2Url = new Uri("https://dev.azure.com/" + Environment.GetEnvironmentVariable("ORG2"));
            String project2 = Environment.GetEnvironmentVariable("PROJ2");
            String pat2 = Environment.GetEnvironmentVariable("PAT2");

            // Project name which this should look at
            Boolean dryRun = !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DRYRUN"));

            // Create a connection
            var sourceConnection = new VssConnection(org1Url, new VssBasicCredential(string.Empty, pat1));
            var destinationConnection = new VssConnection(org2Url, new VssBasicCredential(string.Empty, pat2));

            var destProject = await destinationConnection.GetClient<ProjectHttpClient>().GetProject(project2);


            var sourceRepos = await GetAllRepos(sourceConnection, project1);
            var destReposDict = (await GetAllRepos(destinationConnection, project2)).ToDictionary(x => x.Name);

            var missingReposInDest = sourceRepos.Where(x => !destReposDict.ContainsKey(getDestNameFromSourceName(x.Name)));

            var tempPath = Path.GetTempPath();
            var tempDir = Directory.CreateDirectory(Path.Join(tempPath, "repocloner", RandomString(5).ToLower()));

            Console.WriteLine($"Using path {tempDir.FullName}");

            // Create any repos missing in proj2
            var destSourceClient = destinationConnection.GetClient<GitHttpClient>();
            foreach (var sourceRepo in missingReposInDest)
            {
                if (dryRun)
                {
                    Console.WriteLine($"Would create repo {sourceRepo.Name} in {destSourceClient.BaseAddress}");
                }
                else
                {

                    var gitToCreate = new GitRepositoryCreateOptions()
                    {
                        Name = getDestNameFromSourceName(sourceRepo.Name),
                        ProjectReference = destProject,
                    };
                    var destRepo = await destSourceClient.CreateRepositoryAsync(gitToCreate);
                    destReposDict[destRepo.Name] = destRepo;
                }
            }

            // Sync source from proj1 to proj2 for all branchs
            foreach (var sourceRepo in sourceRepos)
            {
                var destRepo = destReposDict[getDestNameFromSourceName(sourceRepo.Name)];
                if (dryRun)
                {
                    Console.WriteLine($"Would clone {sourceRepo.Name} @ {sourceRepo.WebUrl} and sync to {destRepo.Name} @ {destRepo.WebUrl}");
                }
                else
                {

                    // Create a temp dir to store the cloned repo
                    var gitDir = Directory.CreateDirectory(Path.Join(tempDir.FullName, sourceRepo.Name));

                    var co = new CloneOptions();
                    CredentialsHandler sourceCredProvider = (_url, _user, _cred) => new UsernamePasswordCredentials { Username = pat1, Password = pat1 };
                    co.CredentialsProvider = sourceCredProvider;


                    Console.WriteLine($"Cloning source {sourceRepo.Name}");
                    Repository.Clone(sourceRepo.RemoteUrl, gitDir.FullName, co);

                    using (var repo = new Repository(gitDir.FullName))
                    {
                        CredentialsHandler destCredProvider = (_url, _user, _cred) => new UsernamePasswordCredentials { Username = pat2, Password = pat2 };
                        LibGit2Sharp.PushOptions options = new LibGit2Sharp.PushOptions();
                        options.CredentialsProvider = destCredProvider;

                        if (!repo.Network.Remotes.Where(x => x.Name == "dest").Any())
                        {
                            repo.Network.Remotes.Add("dest", destRepo.RemoteUrl);
                        }
                        else
                        {
                            repo.Network.Remotes.Remove("dest");
                            repo.Network.Remotes.Add("dest", destRepo.RemoteUrl);
                        }

                        var destRemote = repo.Network.Remotes["dest"];

                        foreach (var branch in repo.Branches)
                        {
                            Console.WriteLine($"Pusing source {sourceRepo.Name}:{branch} to {destRepo.Name}:{branch} @ {destRepo.Url}");

                            repo.Network.Push(destRemote, branch.CanonicalName, new PushOptions()
                            {
                                CredentialsProvider = destCredProvider
                            });
                        }

                    }
                }
            }

        }

        private static string getDestNameFromSourceName(String x)
        {
            return "export_" + x;
        }

        static private async Task<IEnumerable<GitRepository>> GetAllRepos(VssConnection connection, String project)
        {
            var sourceClient = connection.GetClient<GitHttpClient>();
            var repos = await sourceClient.GetRepositoriesAsync(project);
            return repos;
        }

        private static Random random = new Random();
        public static string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, length)
              .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}

