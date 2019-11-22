﻿namespace Unity.Editor.ProcessServer.Server
{
    using Ipc;

    public class ServerConfiguration : Configuration
    {
        public bool Debug { get; set; }
        public string ProjectPath { get; set; }
        public string UnityVersion { get; set; }
        public string UnityApplicationPath { get; set; }
        public string UnityContentsPath { get; set; }
    }
}