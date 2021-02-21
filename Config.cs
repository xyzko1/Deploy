using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Deploy
{
    class Config
    {
        public string[] globals { get; set; }
        public Server[] servers { get; set; }

        public class Server
        {
            public string host;
            public int port;
            public string username;
            public string password;

            public Package[] packages;

            public class Package
            {
                public bool enable;
                public string[] commands;
                public int order;
                public string[] variables;
            }
        }
    }
}
