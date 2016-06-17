﻿using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using Microsoft.Rest;
using Microsoft.Azure.Management.ResourceManager;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Azure;
using Microsoft.Azure.Management.ResourceManager.Models;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Configuration;
using System.Net;
using System.Text;
using System.Web;
using Microsoft.Research.MultiWorldTesting.Contract;
using System.Threading;

namespace Microsoft.Research.DecisionServiceTest
{
    public class ProvisioningBaseTest
    {
        private JObject deploymentOutput;

        protected bool deleteOnCleanup;
        protected string managementCenterUrl;
        protected string managementPassword;
        protected string onlineTrainerUrl;
        protected string onlineTrainerToken;
        protected string webServiceUrl;
        protected string webServiceToken;
        protected string settingsUrl;

        private static string GetConfiguration(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value != null)
                return value;

            return ConfigurationManager.AppSettings[name];
        }

        protected ProvisioningBaseTest()
        {
            this.deleteOnCleanup = true;
        }

        protected ProvisioningBaseTest(string deploymentOutput)
        {
            this.deleteOnCleanup = false;
            this.deploymentOutput = JObject.Parse(deploymentOutput);
            this.ParseDeploymentOutputs();
        }

        protected void ConfigureDecisionService(string trainArguments = null, float? initialExplorationEpsilon = null, bool? isExplorationEnabled = null)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add($"Authorization: {managementPassword}");
                var query = new List<string>();
                if (trainArguments != null)
                    query.Add("trainArguments=" + HttpUtility.UrlEncode(trainArguments));
                if (initialExplorationEpsilon != null)
                    query.Add("initialExplorationEpsilon=" + initialExplorationEpsilon);
                if (isExplorationEnabled != null)
                    query.Add("isExplorationEnabled=" + isExplorationEnabled);

                var url = managementCenterUrl + "/Automation/UpdateSettings";
                if (query.Count > 0)
                    url += "?" + string.Join("&", query);

                wc.DownloadString(url);
            }

            // validate
            var metaData = ApplicationMetadataUtil.DownloadMetadata<ApplicationClientMetadata>(settingsUrl);
            if (trainArguments != null)
                Assert.AreEqual(trainArguments, metaData.TrainArguments);

            if (initialExplorationEpsilon != null)
                Assert.AreEqual((float)initialExplorationEpsilon, metaData.InitialExplorationEpsilon);

            if (isExplorationEnabled != null)
                Assert.AreEqual((bool)isExplorationEnabled, metaData.IsExplorationEnabled);
        }

        protected void OnlineTrainerReset()
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add($"Authorization: {onlineTrainerToken}");
                wc.DownloadString($"{onlineTrainerUrl}/reset");
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }

        private void ParseDeploymentOutputs()
        {
            this.managementCenterUrl = this.deploymentOutput["management Center URL"]["value"].ToObject<string>();
            this.managementPassword = this.deploymentOutput["management Center Password"]["value"].ToObject<string>();
            this.onlineTrainerUrl = this.deploymentOutput["online Trainer URL"]["value"].ToObject<string>();
            this.onlineTrainerToken = this.deploymentOutput["online Trainer Token"]["value"].ToObject<string>();
            this.webServiceUrl = this.deploymentOutput["web Service URL"]["value"].ToObject<string>();
            this.webServiceToken = this.deploymentOutput["web Service Token"]["value"].ToObject<string>();
            this.settingsUrl = this.deploymentOutput["client Library Url"]["value"].ToObject<string>();
        }

        private ResourceManagementClient CreateResourceManagementClient()
        {
            string clientId = GetConfiguration("ClientId");
            string passwd = GetConfiguration("Password");
            string tenantId = GetConfiguration("TenantId");
            string subscriptionId = GetConfiguration("SubscriptionId");

            var clientCredentials = new ClientCredential(clientId, passwd);
            var authorityUri = "https://login.windows.net/" + tenantId;
            var context = new AuthenticationContext(authorityUri);
            var authResult = context.AcquireTokenAsync("https://management.azure.com/", clientCredentials);
            authResult.Wait();

            var tokenCredentials = new TokenCloudCredentials(subscriptionId, authResult.Result.AccessToken);
            var credentialsForRM = new TokenCredentials(tokenCredentials.Token);

            var armClient = new ResourceManagementClient(credentialsForRM);
            armClient.SubscriptionId = subscriptionId;

            return armClient;
        }

        [TestInitialize]
        public void Initialize()
        {
            // re-using deployment
            if (this.deploymentOutput != null)
                return;

            Cleanup();

            var armClient = this.CreateResourceManagementClient();
            
            var prefix = GetConfiguration("prefix");

            var resourceGroupName = prefix + Guid.NewGuid().ToString().Replace("-","").Substring(4);
            var deploymentName = resourceGroupName + "_deployment";
            var location = "eastus";

            // create resource group
            var rgResult = armClient.ResourceGroups.CreateOrUpdate(resourceGroupName, new ResourceGroup { Location = location });

            Assert.AreEqual("Succeeded", rgResult.Properties.ProvisioningState);

            DeploymentOperation failedOperation = null;
            using (var poller = Observable
                .Interval(TimeSpan.FromSeconds(2))
                .Delay(TimeSpan.FromSeconds(5))
                .SelectMany(async _ =>
                {
                    try
                    {
                        var operations = await armClient.DeploymentOperations.ListAsync(resourceGroupName, deploymentName);

                        // {op.Properties.TargetResource.Id}
                        foreach (var op in operations)
                        {
                            if (op == null || op.Properties == null || op.Properties.ProvisioningState == null)
                                continue;

                            Trace.WriteLine($"Status: {op?.Properties?.TargetResource?.ResourceName,-30} '{op?.Properties?.ProvisioningState}'");
                            switch(op.Properties.ProvisioningState)
                            {
                                case "Running":
                                case "Succeeded":
                                    break;
                                default:
                                    failedOperation = op;
                                    break;
                            }
                        }
                        Trace.WriteLine("");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine("Status polling failed: " + ex.Message);
                    }

                    return Task.FromResult(true);
                })
                .Subscribe())
            {
                var deployment = new Deployment();
                deployment.Properties = new DeploymentProperties
                {
                    Mode = DeploymentMode.Incremental,
                    TemplateLink = new TemplateLink("https://raw.githubusercontent.com/Microsoft/mwt-ds-provisioning/master/azuredeploy.json"),
                    Parameters = JObject.Parse("{\"number Of Actions\":{\"value\":0}}")
                };

                try
                {
                    var dpResult = armClient.Deployments.CreateOrUpdate(resourceGroupName, deploymentName, deployment);
                    this.deploymentOutput = (JObject)dpResult.Properties.Outputs;

                    // make test case copy paste easy
                    Console.WriteLine(deploymentOutput.ToString(Newtonsoft.Json.Formatting.Indented).Replace("\"", "\"\""));

                    this.ParseDeploymentOutputs();
                }
                catch (AggregateException ae)
                {
                    // Go through all exceptions and dump useful information
                    ae.Handle(x =>
                    {
                        Trace.WriteLine(x);
                        return false;
                    });
                }
            }

            Assert.IsNull(failedOperation, $"Deployment operation failed: '{failedOperation}'");

            // give the deployment a bit of time to spin up completely
            Thread.Sleep(TimeSpan.FromMinutes(2));
        }

        //[TestCleanup]
        public void Cleanup()
        {
            if (!deleteOnCleanup)
                return;

            try
            {
                var armClient = this.CreateResourceManagementClient();
                var prefix = GetConfiguration("prefix");
                var oldResources = armClient.ResourceGroups.List().Where(r => r.Name.StartsWith(prefix));

                foreach (var resource in oldResources)
                {
                    var task = armClient.ResourceGroups.DeleteAsync(resource.Name);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
