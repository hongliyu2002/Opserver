﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.DirectoryServices.ActiveDirectory;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using StackExchange.Opserver.Monitoring;

namespace StackExchange.Opserver.Data.Dashboard.Providers
{
    partial class WmiDataProvider
    {
        private partial class WmiNode
        {
            private static readonly string MachineDomainName;
 
            static WmiNode()
            {
                try
                {
                    // throws a 
                    var d = Domain.GetComputerDomain();
                    MachineDomainName = d.Name;
                }
                catch (ActiveDirectoryObjectNotFoundException) { }
                catch (Exception e)
                {
                    Current.LogException(e);
                }
            }
 
            public async Task<Node> PollNodeInfoAsync()
            {
                try
                {
                    // TODO: Check concurrency options for a Task.WaitAll
                    await UpdateNodeDataAsync().ConfigureAwait(false);
                    await GetAllInterfacesAsync().ConfigureAwait(false);
                    await GetAllVolumesAsync().ConfigureAwait(false);
                    SetReferences();
                }
                catch (COMException e)
                {
                    Current.LogException(e);
                    Status = NodeStatus.Unreachable;
                }
                return this;
            }

            public async Task<Node> PollStats()
            {
                try
                {
                    // TODO: Check concurrency options for a Task.WaitAll
                    await PollCpuUtilizationAsync().ConfigureAwait(false);
                    await PollMemoryUtilizationAsync().ConfigureAwait(false);
                    await PollNetworkUtilizationAsync().ConfigureAwait(false);
                }
                catch (COMException e)
                {
                    Current.LogException(e);
                    Status = NodeStatus.Unreachable;
                }
                return this;
            }

            private async Task UpdateNodeDataAsync()
            {
                const string machineQuery = @"select 
                DNSHostName,
                Domain,
                Manufacturer,
                Model
                from Win32_ComputerSystem";
                using (var q = Wmi.Query(Name, machineQuery))
                {
                    var data = await q.GetFirstResultAsync().ConfigureAwait(false);
                    if (data == null)
                        return;
                    Model = data.Model;
                    Manufacturer = data.Manufacturer;
                    // Only use domain if we're on one - not for things like workgroups
                    Name = MachineDomainName.HasValue() && data.Domain != MachineDomainName
                        ? $"{data.DNSHostName}.{data.Domain}"
                        : data.DNSHostName;
                }

                const string query = @"select 
                Caption,
                LastBootUpTime,
                Version,
                FreePhysicalMemory,
                TotalVisibleMemorySize,
                Version
                from Win32_OperatingSystem";

                using (var q = Wmi.Query(Name, query))
                {
                    var data = await q.GetFirstResultAsync().ConfigureAwait(false);
                    if (data == null)
                        return;
                    LastBoot = ManagementDateTimeConverter.ToDateTime(data.LastBootUpTime);
                    TotalMemory = data.TotalVisibleMemorySize * 1024;
                    MemoryUsed = TotalMemory - data.FreePhysicalMemory * 1024;
                    KernelVersion = Version.Parse(data.Version);
                    MachineType = data.Caption.ToString() + " " + data.Version.ToString();
                }

                LastSync = DateTime.UtcNow;
                Status = NodeStatus.Active;
            }

            private async Task GetAllInterfacesAsync()
            {
                //if (KernelVersion > WindowsKernelVersions.Windows2012And8)
                //{
                //    //ActiveMaximumTransmissionUnit
                //    //MtuSize
                //    //
                //    //Speed
                //}

                const string query = @"
SELECT Name,
       DeviceID,
       NetConnectionID,
       Description,
       MACAddress,
       Speed
  FROM Win32_NetworkAdapter
 WHERE NetConnectionStatus = 2"; //connected adapters.
                //'AND PhysicalAdapter = True' causes exceptions with old windows versions.

                using (var q = Wmi.Query(Name, query))
                {
                    foreach (var data in await q.GetDynamicResultAsync().ConfigureAwait(false))
                    {
                        string id = $"{data.DeviceID}";
                        var i = Interfaces.FirstOrDefault(x => x.Id == id);
                        if (i == null)
                        {
                            i = new Interface();
                            Interfaces.Add(i);
                        }

                        i.Id = $"{data.DeviceID}";
                        i.Alias = "!alias";
                        i.Caption = data.NetConnectionID == "Ethernet" ? data.Name : data.NetConnectionID;
                        i.FullName = data.Description;
                        i.NodeId = Id;
                        i.LastSync = DateTime.UtcNow;
                        i.Name = data.Name;
                        i.PhysicalAddress = data.MACAddress;
                        i.Speed = data.Speed;
                        i.Status = NodeStatus.Active;
                        i.TypeDescription = "";
                        // TODO: Implement on less-frequent queries => bulk and getter override?
                        i.IPs = new List<IPNet>();
                    }
                }
            }

            private async Task GetAllVolumesAsync()
            {
                const string query = @"
SELECT Caption,
       DeviceID,
       Description,
       FreeSpace,
       Name,
       Size,
       VolumeSerialNumber
  FROM Win32_LogicalDisk
 WHERE DriveType = 3"; //fixed disks

                using (var q = Wmi.Query(Name, query))
                {
                    foreach (var disk in await q.GetDynamicResultAsync().ConfigureAwait(false))
                    {
                        var id = $"{disk.DeviceID}";
                        var v = Volumes.FirstOrDefault(x => x.Id == id);
                        if (v == null)
                        {
                            v = new Volume();
                            Volumes.Add(v);
                        }
                        
                        v.Id = $"{disk.DeviceID}";
                        v.Available = disk.FreeSpace;
                        v.Caption = disk.VolumeSerialNumber;
                        v.Description = disk.Name + " - " + disk.Description;
                        v.Name = disk.Name;
                        v.NodeId = Id;
                        v.Size = disk.Size;
                        v.Type = "Fixed Disk";
                        v.Status = NodeStatus.Active;
                        v.Used = v.Size - v.Available;
                        if (v.Size > 0)
                        {
                            v.PercentUsed = (float)(100 * v.Used / v.Size);
                        }
                    }
                }
            }
            
            private async Task PollCpuUtilizationAsync()
            {
                const string query = @"
SELECT PercentProcessorTime 
  FROM Win32_PerfFormattedData_PerfOS_Processor
 WHERE Name = '_Total'";

                using (var q = Wmi.Query(Name, query))
                {
                    var data = await q.GetFirstResultAsync().ConfigureAwait(false);
                    if (data == null)
                        return;
                
                    CPULoad = (short)data.PercentProcessorTime;
                    var cpuUtilization = new CPUUtilization
                    {
                        DateEpoch = DateTime.UtcNow.ToEpochTime(),
                        AvgLoad = CPULoad
                    };
                    UpdateHistoryStorage(CPUHistory, cpuUtilization);
                }
            }

            private async Task PollMemoryUtilizationAsync()
            {
                const string query = @"
SELECT AvailableKBytes 
  FROM Win32_PerfFormattedData_PerfOS_Memory";
                
                using (var q = Wmi.Query(Name, query))
                {
                    var data = await q.GetFirstResultAsync().ConfigureAwait(false);
                    if (data == null)
                        return;

                    var available = data.AvailableKBytes * 1024;
                    MemoryUsed = TotalMemory - available;
                    var utilization = new MemoryUtilization
                    {
                        DateEpoch = DateTime.UtcNow.ToEpochTime(),
                        AvgMemoryUsed = MemoryUsed
                    };
                    UpdateHistoryStorage(MemoryHistory, utilization);
                }
            }

            private static readonly ConcurrentDictionary<string, string> CounterLookup = new ConcurrentDictionary<string, string>();

            private static string GetCounterName(string original)
            {
                return CounterLookup.GetOrAdd(original,
                    k => StringBuilderCache.Get()
                        .Append(k)
                        .Replace("\\", "_")
                        .Replace("/", "_")
                        .Replace("(", "[")
                        .Replace(")", "]")
                        .Replace("#", "_")
                        .ToStringRecycle());
            }

            private async Task PollNetworkUtilizationAsync()
            {
                const string query = @"
SELECT Name,
       BytesReceivedPersec,
       BytesSentPersec,
       PacketsReceivedPersec,
       PacketsSentPersec
  FROM Win32_PerfFormattedData_Tcpip_NetworkInterface";

                var queryTime = DateTime.UtcNow.ToEpochTime();
                var combinedUtil = new Interface.InterfaceUtilization
                {
                    DateEpoch = queryTime,
                    InAvgBps = 0,
                    OutAvgBps = 0
                };

                using (var q = Wmi.Query(Name, query))
                {
                    foreach (var data in await q.GetDynamicResultAsync().ConfigureAwait(false))
                    {
                        if (data == null) continue;
                        var iface = Interfaces.FirstOrDefault(i => data.Name == GetCounterName(i.Name));
                        if (iface == null) continue;

                        iface.InBps = data.BytesReceivedPersec;
                        iface.OutBps = data.BytesSentPersec;
                        iface.InPps = data.PacketsReceivedPersec;
                        iface.OutPps = data.PacketsSentPersec;

                        var util = new Interface.InterfaceUtilization
                        {
                            DateEpoch = queryTime,
                            InAvgBps = iface.InBps,
                            OutAvgBps = iface.OutBps
                        };

                        var netData = NetHistory.GetOrAdd(iface.Name, k => new List<Interface.InterfaceUtilization>(1024));
                        UpdateHistoryStorage(netData, util);

                        if (PrimaryInterfaces.Contains(iface))
                        {
                            combinedUtil.InAvgBps += util.InAvgBps;
                            combinedUtil.OutAvgBps += util.OutAvgBps;
                        }
                    }
                }
                UpdateHistoryStorage(CombinedNetHistory, combinedUtil);
            }
        }
    }
}