﻿using Newtonsoft.Json;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Deploy
{
    class Program
    {
        const string GLOBAL = "GLOBAL";
        static void Main(string[] args)
        {
            string json = args.Length == 0 ? "app.json" : args[0];
            StreamReader sr = new StreamReader(json, Encoding.UTF8);
            var t = sr.ReadToEnd();
            var config = JsonConvert.DeserializeObject<Config>(t);
            sr.Dispose();

            List<ServerTask> tasks = LoadTasks(config);
            if (tasks.Count == 0)
            {
                Console.WriteLine("无任何指令需要执行，按任意键退出..");
                Console.ReadKey();
                return;
            }
            foreach (var server in tasks)
            {
                Console.WriteLine("服务器 {0} ================================执行命令================================", server.host);
                foreach (var c in server.commands)
                {
                    Console.WriteLine(c);
                }
            }
            Console.Write("确认执行上述命令吗？y/N:");
            if (Console.ReadKey().Key != ConsoleKey.Y)
            {
                Console.WriteLine("\n请按任意键退出..");
                Console.ReadKey();
                return;
            }
            Console.WriteLine("");

            foreach (var server in tasks)
            {
                try
                {
                    RunServer(server);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("发生错误：");
                    Console.WriteLine(ex.Message);
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine(ex.InnerException.Message);
                    }
                    break;
                }
            }

            Console.WriteLine("全部执行完毕，按任意键退出..");
            Console.ReadKey();
        }

        static List<ServerTask> LoadTasks(Config config)
        {
            List<ServerTask> tasks = new List<ServerTask>();
            foreach (var s in config.servers)
            {
                var packages = s.packages.Where(p => p.enable).OrderBy(p => p.order).ToArray();
                if (packages.Length > 0)
                {
                    ServerTask t = new ServerTask();
                    t.host = s.host;
                    t.port = s.port;
                    t.username = s.username;
                    t.password = s.password;
                    t.commands = new List<string>();
                    foreach (var package in packages)
                    {
                        var variables = package.variables;
                        if (variables != null)
                        {
                            variables = variables.Select(p =>
                            {
                                if (p.StartsWith(GLOBAL))
                                {
                                    return config.globals[int.Parse(p.Substring(6))];
                                }
                                return p;
                            }).ToArray();
                        }
                        foreach (var c in package.commands)
                        {
                            string command = c;
                            if (variables != null)
                            {
                                command = Regex.Replace(c, @"\{\d+\}", m =>
                                {
                                    int i = int.Parse(m.Value.Trim('{').Trim('}'));
                                    return variables[i];
                                });
                            }
                            t.commands.Add(command);
                        }
                    }
                    tasks.Add(t);
                }
            }
            return tasks;
        }

        static void RunServer(ServerTask server)
        {
            Console.WriteLine("开始连接服务器:" + server.host);
            using (var sshClient = new SshClient(server.host, server.port, server.username, server.password))
            {
                sshClient.Connect();
                Console.WriteLine("服务器{0}连接成功！", server.host);
                foreach (var command in server.commands)
                {
                    Console.WriteLine("开始执行命令:" + command);
                    var arr = command.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                    if (arr.Length == 3 && arr[0].ToLower() == "upload")
                    {
                        Upload(server, arr[1], arr[2]);
                    }
                    else if (arr.Length == 3 && arr[0].ToLower().StartsWith("uploaddir"))
                    {
                        var s = arr[0].Substring(9);
                        UploadDir(server, arr[1], arr[2], string.IsNullOrEmpty(s) ? 0 : int.Parse(s));
                    }
                    else if (arr.Length == 2 && arr[0].ToLower() == "bak")
                    {
                        Bak(sshClient, arr[1]);
                    }
                    else if (arr.Length >= 3 && arr[0].ToLower() == "zipbak")
                    {

                        ZipBak(sshClient, arr[1], arr.Skip(2));
                    }
                    else
                    {
                        ExcuteCommand(sshClient, command);
                    }
                }
            }
        }



        static void UploadDir(ServerTask server, string sourceDir, string targetDir, int count)
        {
            Console.WriteLine("开始上传文件夹 {0} 到 {1}", sourceDir, targetDir);
            DirectoryInfo di = new DirectoryInfo(sourceDir);
            if (!di.Exists)
                throw new Exception($"文件夹{sourceDir}不存在");
            IEnumerable<FileInfo> files = di.GetFiles();
            if (count > 0)
                files = files.OrderByDescending(p => p.LastWriteTime).Take(count);
            foreach (var file in files)
            {
                Upload(server, file.FullName, Path.Combine(targetDir, file.Name));
            }
        }

        static void Upload(ServerTask server, string sourcePath, string targetPath)
        {
            Console.WriteLine("开始上传文件 {0} 到 {1}", sourcePath, targetPath);
            using (var sftpClient = new SftpClient(server.host, server.port, server.username, server.password))
            {
                sftpClient.Connect();
                var fs = File.Open(sourcePath, FileMode.Open);
                ulong total = (ulong)fs.Length;
                sftpClient.UploadFile(fs, targetPath, current =>
                {
                    Console.Write("上传进度: {0}/{1}----------{2}%\r", current, total, current * 100 / total);
                });
            }
            Console.WriteLine("\n文件上传完成");
        }

        static void Bak(SshClient sshClient, string filePath)
        {
            Console.WriteLine("开始备份文件 {0}", filePath);
            var bakPath = string.Format("{0}-{1:yyyyMMdd}", filePath, DateTime.Now);
            var res = ExcuteCommand(sshClient, "ls -al " + bakPath);
            if (string.IsNullOrEmpty(res))
            {
                var command = string.Format("mv {0} {1}", filePath, bakPath);
                ExcuteCommand(sshClient, command);
                Console.WriteLine("{0}备份完成！", filePath);
            }
            else
            {
                Console.WriteLine("文件已存在，跳过备份！");
            }
        }

        static void ZipBak(SshClient sshClient, string targetZip, IEnumerable<string> paths)
        {
            Console.WriteLine("开始进行zip备份 {0}", targetZip);
            var bakPath = string.Format("{0}.{1:yyyyMMdd}.bak.zip", targetZip, DateTime.Now);
            var res = ExcuteCommand(sshClient, "ls -al " + bakPath);
            if (string.IsNullOrEmpty(res))
            {
                var command = @"$(type zip | awk '{print $3}') -r " + bakPath;
                foreach (var p in paths)
                {
                    command += " " + p;
                }
                ExcuteCommand(sshClient, command);
                Console.WriteLine("{0}备份完成！", targetZip);
            }
            else
            {
                Console.WriteLine("文件已存在，跳过备份！");
            }
        }

        static string ExcuteCommand(SshClient sshClient, string command)
        {
            Console.WriteLine("执行命令：" + command);
            using (var cmd = sshClient.CreateCommand(command))
            {
                var res = cmd.Execute();
                Console.WriteLine("命令返回：" + res);
                return res;
            }
        }
    }
}
