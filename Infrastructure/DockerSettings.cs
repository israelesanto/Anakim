using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnakimOrchestrator.Infrastructure
{
    public class DockerSettings
    {
        public bool Enabled { get; set; }
        public string? ContainerDnsName { get; set; }
        public bool UseInternalNetwork { get; set; }
        public bool PreferContainerIp { get; set; }
    }

}
