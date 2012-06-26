﻿using Microsoft.VisualBasic;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using RemoteZip;
using PlistCS;
using XGetoptCS;

namespace iKGD
{
	internal sealed class iKGD
	{
		public static string Version = "1.0";
		public static string TempDir = Path.GetTempPath() + @"iKGD\";
		public static string IPSWdir = TempDir + @"IPSW\";
		public static string Resources = TempDir + @"Resources\";
		public static string KeysDir = @"C:\IPSW\Keys\";
		public static string CurrentProcessName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);
		public static string DropboxDir = ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(
			File.ReadAllLines(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox\\host.db"))[1])) + "\\";
		public static string RemoteFileLocation = DropboxDir + "share\\";

		public static string IPSWLocation, IPSWurl, ReqDevice, ReqFirmware = "";
		public static bool RunningRemotelyServer = false;
		public static bool RunningRemotelyHome = false;
		public static bool RebootDevice = true;
		public static bool Verbose = false;

		public static string RootFileSystem, RestoreRamdisk, UpdateRamdisk = "";
		public static bool RestoreRamdiskIsEncrypted, RestoreRamdiskExists, UpdateRamdiskIsEncrypted, UpdateRamdiskExists;
		public static bool ExtractFullRootFS = false;

		public static string Device, Firmware, BuildID, Codename, Platform, BoardConfig, PluggedInDevice, DownloadURL, VFDecryptKey, Baseband = "";
		public static bool BasebandExists = false;

		public static string[] images = new string[] { "UpdateRamdisk", "RestoreRamdisk", "AppleLogo", "BatteryCharging0", "BatteryCharging1", "BatteryFull", "BatteryLow0", "BatteryLow1", "DeviceTree", "BatteryCharging", "BatteryPlugin", "iBEC", "iBoot", "iBSS", "KernelCache", "LLB", "RecoveryMode" };
		public static string[] kbags = new string[images.Length];
		public static string[] iv = new string[images.Length];
		public static string[] key = new string[images.Length];

		static void Main(string[] args)
		{
			Console.WriteLine("\nInitializing iKGD v" + Version);

			char c;
			XGetopt g = new XGetopt();
			while ((c = g.Getopt(args.Length, args, "evi:u:d:f:rSH")) != '\0')
			{
				switch (c)
				{
					case 'i': IPSWLocation = g.Optarg; break;
					case 'u': IPSWurl = g.Optarg; break;
					case 'd': ReqDevice = g.Optarg; break;
					case 'f': ReqFirmware = g.Optarg; break;
					case 'k': KeysDir = g.Optarg; break;
					case 'r': RebootDevice = false; break;
					case 'e': ExtractFullRootFS = true; break;
					case 'S': RunningRemotelyServer = true; break;
					case 'H': RunningRemotelyHome = true; break;
					case 'v': Verbose = true; break;
				}
			}


			if (RunningRemotelyHome)
			{
				RemoteModeHome();
			}
			else if (!string.IsNullOrEmpty(IPSWLocation))
			{
				if (!FileIO.File_Exists(IPSWLocation))
				{
					Console.WriteLine("File {0} does not exist!", IPSWLocation);
					Environment.Exit((int)ExitCode.InvalidIPSWLocation);
				}
			}
			else if (!string.IsNullOrEmpty(IPSWurl))
			{
				ExtractFullRootFS = false;
				if (!Remote.isURLaFirmware(IPSWurl))
				{
					Console.WriteLine("The url specified is not a valid iOS firmware.");
					Environment.Exit((int)ExitCode.URLisNotFirmware);
				}
			}
			else if (!string.IsNullOrEmpty(ReqDevice) && !string.IsNullOrEmpty(ReqFirmware))
			{
				Console.Write("Fetching link...");
				IPSWurl = Utils.GetFirmwareURL(ReqDevice, ReqFirmware);
				if (!Remote.isURLaFirmware(IPSWurl))
				{
					Console.WriteLine("\tInvalid URL!\n\nPlease double check if the specified firmware exists for the device.");
					Environment.Exit((int)ExitCode.InvalidURL);
				}
				Console.WriteLine();
			}
			else
			{
				PrintUsage(CurrentProcessName);
			}

			Console.WriteLine("Checking resources...");
			Utils.CheckResources();

			Stopwatch timer = new Stopwatch();
			timer.Start();

			if (!string.IsNullOrEmpty(IPSWLocation))
				ExtractIPSW();

			if (!string.IsNullOrEmpty(IPSWurl))
				DownloadIPSW();

			CheckRamdisks();

			GrabKBAGS();

			if (RunningRemotelyServer)
				RemoteModeServer();

			else
			{
				MakeDeviceReady();
				GrabKeys();
				if (RebootDevice) Utils.irecovery("-kick");
			}

			DecryptRamdisks();

			GetVFDecryptKey();

			GetBaseband();

			FetchFirmwareURL();

			MakeFilesForKeys();

			CopyKeysToKeysDir();

			DecryptRootFS();

			timer.Stop();
			Console.WriteLine("Elapsed for {0} seconds", (double) timer.ElapsedMilliseconds / 1000);

			Environment.Exit((int)ExitCode.Success);
		}

		public static void ExtractIPSW()
		{
			Console.WriteLine("Firmware: " + Path.GetFileNameWithoutExtension(IPSWLocation));
			Console.Write("Extracting essential files from zip...");
			Utils.UnzipFile(IPSWLocation, IPSWdir, "Restore.plist");
			Utils.UnzipFile(IPSWLocation, IPSWdir, "BuildManifest.plist");
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			Console.Write("Parsing Restore.plist...");
			Utils.ParseRestorePlist(IPSWdir + "Restore.plist");
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			Console.Write("Extracting images...");
			for (int i = 2; i < images.Length; i++)
			{
				Utils.UnzipFile(IPSWLocation, IPSWdir, Utils.GetImagePathFromBuildManifest(images[i], IPSWdir + "BuildManifest.plist"));
			}
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			Console.Write("Extracting ramdisks and root filesystem...");
			if (ExtractFullRootFS) Utils.UnzipFile(IPSWLocation, IPSWdir, RootFileSystem);
			if (!ExtractFullRootFS) Utils.UnzipFile(IPSWLocation, IPSWdir, RootFileSystem, 122880);
			Utils.UnzipFile(IPSWLocation, IPSWdir, UpdateRamdisk);
			Utils.UnzipFile(IPSWLocation, IPSWdir, RestoreRamdisk);
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
		}

		public static void DownloadIPSW()
		{
			try
			{
				FileIO.Directory_Create(IPSWdir);
				Console.WriteLine("Firmware: " + Path.GetFileNameWithoutExtension(IPSWurl));
				Console.Write("Downloading essential files...");
				Remote.DownloadFileFromZip(IPSWurl, "Restore.plist", IPSWdir + "Restore.plist");
				Remote.DownloadFileFromZip(IPSWurl, "BuildManifest.plist", IPSWdir + "BuildManifest.plist");
				Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
				Console.WriteLine("Parsing Restore.plist");
				Utils.ParseRestorePlist(IPSWdir + "Restore.plist");
				Console.Write("Downloading images...");
				for (int i = 2; i < images.Length; i++)
				{
					string img = Utils.GetImagePathFromBuildManifest(images[i], IPSWdir + "BuildManifest.plist");
					if (Verbose) Console.WriteLine("\r[v] Downloading " + Path.GetFileName(img));
					Remote.DownloadFileFromZip(IPSWurl, img, IPSWdir + Path.GetFileName(img));
				}
				if (!Verbose) Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
				Console.Write("Downloading ramdisks and root filesystem...");
				if (Verbose) Console.WriteLine("\r[v] Downloading root filesystem");
				Remote.DownloadFileFromZipInBackground(IPSWurl, RootFileSystem, IPSWdir + RootFileSystem, 125829120);
				if (Verbose) Console.WriteLine("[v] Downloading update ramdisk");
				Remote.DownloadFileFromZip(IPSWurl, UpdateRamdisk, IPSWdir + UpdateRamdisk);
				if (Verbose) Console.WriteLine("[v] Downloading restore ramdisk");
				Remote.DownloadFileFromZip(IPSWurl, RestoreRamdisk, IPSWdir + RestoreRamdisk);
				if (!Verbose) Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			}
			catch (Exception e)
			{
				if (Verbose) Console.WriteLine(e);
			}
		}

		public static void CheckRamdisks()
		{
			UpdateRamdiskExists = (!string.IsNullOrEmpty(UpdateRamdisk));
			RestoreRamdiskExists = (!string.IsNullOrEmpty(RestoreRamdisk));
			Console.Write("Checking if ramdisks are encrypted...");
			kbags[0] = Utils.xpwntool(IPSWdir + UpdateRamdisk, TempDir + "rd2.dmg").Trim();
			kbags[1] = Utils.xpwntool(IPSWdir + RestoreRamdisk, TempDir + "rd1.dmg").Trim();
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			UpdateRamdiskIsEncrypted = (kbags[0].Length != 0) && UpdateRamdiskExists;
			RestoreRamdiskIsEncrypted = (kbags[1].Length != 0) && RestoreRamdiskExists;
			Console.WriteLine("Update ramdisk: " + (UpdateRamdiskExists ? (UpdateRamdiskIsEncrypted ? "encrypted" : "decrypted") : "not found"));
			Console.WriteLine("Restore ramdisk: " + (RestoreRamdiskExists ? (RestoreRamdiskIsEncrypted ? "encrypted" : "decrypted") : "not found"));
		}

		public static void GrabKBAGS()
		{
			Console.Write("Grabbing kbags...");
			for (int i = 2; i < images.Length; i++)
			{
				string kbagStr = Utils.xpwntool(IPSWdir + Path.GetFileName(Utils.GetImagePathFromBuildManifest(images[i], IPSWdir + "BuildManifest.plist")), "/dev/null");
				kbags[i] = kbagStr.Substring(0, kbagStr.IndexOf(Environment.NewLine));
			}
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
		}

		public static void MakeDeviceReady()
		{
			Utils.irecovery("-killitunes");
			if (!Utils.irecovery_getenv("iKGD").Contains("true"))
			{
				int count = 1;
				Console.Write("Waiting for device in DFU mode...");
				while (!Utils.SearchDeviceInMode("DFU"))
				{
					Console.CursorLeft = 0;
					Console.Write("Waiting for device in DFU mode...  [{0}]", count);
					Utils.Delay(1);
					count++;
				}
				PluggedInDevice = Utils.irecovery("-getboardid").Trim();
				Console.CursorLeft = 0;
				Console.WriteLine("Waiting for device in DFU mode...   [Found {0}]", PluggedInDevice);
				if (!Utils.DeviceIsCompatible(PluggedInDevice))
				{
					Console.WriteLine("\nERROR: {0} is not compatible with iKGD yet!\n", PluggedInDevice);
					Environment.Exit((int)ExitCode.IncompatibleDevice);
				}
				if ((Utils.irecovery("-platform").Trim() != Platform) && (!string.IsNullOrEmpty(Platform)))
				{
					Console.WriteLine("\nERROR: Plugged in device is not the same platform as the ipsw!");
					Console.Write("\nYou plugged in a {0} while you're", Utils.irecovery("-platform").Trim());
					Console.Write("\ntrying to get keys for " + Platform + ".\n\n");
					Environment.Exit((int)ExitCode.PlatformNotSame);
				}
				Utils.PwnDevice(PluggedInDevice);
			}
			else
			{
				Console.WriteLine("Found device running iKGD payload");
			}
			irecv_fbechoikgd();
		}

		public static void GrabKeys()
		{
			Console.Write("Grabbing keys...");
			for (int i = 0; i < images.Length; i++)
			{
				if ((RestoreRamdiskIsEncrypted && i == 1) || (UpdateRamdiskIsEncrypted && i == 0) || (i > 1))
				{
					Utils.irecovery_fbecho(images[i]);
					Utils.irecovery_cmd("go aes dec " + kbags[i]);
					iv[i] = Utils.irecovery_getenv("iv").Trim();
					key[i] = Utils.irecovery_getenv("key").Trim();
					Utils.irecovery_fbecho("IV: " + iv[i]);
					Utils.irecovery_fbecho("Key: " + key[i]);
					Utils.irecovery_fbecho("=========================");
				}
			}
			Utils.ConsoleWriteLine((iv[9].Contains("0x") || string.IsNullOrEmpty(iv[9]) ? "   [FAILED]" : "   [DONE]"), ConsoleColor.DarkGray);
		}

		public static void DecryptRamdisks()
		{
			Console.Write("Decrypting ramdisks...");
			if (UpdateRamdiskIsEncrypted) Utils.xpwntool(IPSWdir + UpdateRamdisk, TempDir + "rd2.dmg", iv[0], key[0]);
			if (RestoreRamdiskIsEncrypted) Utils.xpwntool(IPSWdir + RestoreRamdisk, TempDir + "rd1.dmg", iv[1], key[1]);
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
		}

		public static void GetVFDecryptKey()
		{
			Console.Write("Getting vfdecryptkey...");
			try
			{
				string[] vf = Utils.genpass(Platform, TempDir + "rd1.dmg", IPSWdir + RootFileSystem).Split(new string[] { "vfdecrypt key: " }, StringSplitOptions.RemoveEmptyEntries);
				VFDecryptKey = vf[1].Trim();
				if (string.IsNullOrEmpty(VFDecryptKey) && UpdateRamdiskExists)
				{
					vf = Utils.genpass(Platform, TempDir + "rd2.dmg", IPSWdir + RootFileSystem).Split(new string[] { "vfdecrypt key: " }, StringSplitOptions.RemoveEmptyEntries);
					VFDecryptKey = vf[1].Trim();
				}
			}
			catch (Exception) { }
			Utils.ConsoleWriteLine(string.IsNullOrEmpty(VFDecryptKey) ? "   [FAILED]" : "   [DONE]", ConsoleColor.DarkGray);
		}

		public static void GetBaseband()
		{
			Console.Write("Getting baseband...");
			try
			{
				if (Device.Contains("iPhone3,1"))
				{
					BasebandExists = true;
					string[] basebands = Path.GetFileName(Utils.GetImagePathFromBuildManifest("BasebandFirmware", IPSWdir + "BuildManifest.plist")).Split(new string[] { "_" }, StringSplitOptions.None);
					Baseband = basebands[1];
				}
				else if (Device.Contains("iPhone3,3"))
				{
					BasebandExists = true;
					Baseband = Path.GetFileName(Utils.GetImagePathFromBuildManifest("BasebandFirmware", IPSWdir + "BuildManifest.plist")).Replace(".Release.bbfw", "").Replace("Phoenix-", "");
				}
				else if (Device.Contains("iPad1,1"))
				{
					BasebandExists = true;
					FileIO.Directory_Create(TempDir + "bb");
					Utils.hfsplus_extractall(TempDir + "rd2.dmg", "/usr/local/standalone/firmware/", TempDir + "bb");
					string[] bbfw = Directory.GetFiles(TempDir + "bb", "*.bbfw");
					Utils.UnzipAll(bbfw[0], TempDir + "bb");
					string[] baseband = Directory.GetFiles(TempDir + "bb", "*.eep");
					Baseband = Path.GetFileNameWithoutExtension(baseband[1]).Replace("ICE2_", "");
					FileIO.Directory_Delete(TempDir + "bb");
				}
			}
			catch (Exception) { }
			Utils.ConsoleWriteLine(BasebandExists && !string.IsNullOrEmpty(Baseband) ? "   [DONE]" : BasebandExists ? "   [FAILED]" : "   [No Baseband Found]", ConsoleColor.DarkGray);
		}

		public static void FetchFirmwareURL()
		{
			Console.WriteLine("Fetching url for " + Device + " and " + BuildID);
			DownloadURL = Utils.GetFirmwareURL(Device, BuildID);
			Codename = Utils.ParseBuildManifestInfo("BuildTrain", IPSWdir + "BuildManifest.plist");
			if (string.IsNullOrEmpty(DownloadURL))
				Console.WriteLine("Unable to find url. Perhaps " + BuildID + " is a beta firmware?");
		}

		public static void MakeFilesForKeys()
		{
			Console.Write("Making The iPhone Wiki Keys file...");
			Utils.MakeTheiPhoneWikiFile(TempDir + Device + "_" + Firmware + "_" + BuildID + "_Keys.txt");
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);

			Console.Write("Making opensn0w Keys plist...");
			Utils.MakeOpensn0wPlist(TempDir + Device + "_" + Firmware + "_" + BuildID + ".plist");
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
		}

		public static void CopyKeysToKeysDir()
		{
			Console.Write("Copying keys to the keys directory");
			if (FileIO.Directory_Create(KeysDir))
			{
				FileIO.File_Copy(TempDir + Device + "_" + Firmware + "_" + BuildID + "_Keys.txt", KeysDir + Device + "_" + Firmware + "_" + BuildID + "_Keys.txt", true);
				FileIO.File_Copy(TempDir + Device + "_" + Firmware + "_" + BuildID + ".plist", KeysDir + Device + "_" + Firmware + "_" + BuildID + ".plist", true);
			}
			Utils.ConsoleWriteLine(FileIO.File_Exists(KeysDir + Device + "_" + Firmware + "_" + BuildID + "_Keys.txt") ? "   [DONE]" : "   [FAILED]", ConsoleColor.DarkGray);
		}

		public static void DecryptRootFS()
		{
			if (ExtractFullRootFS)
			{
				Console.Write("Decrypting the Root FileSystem...");
				Utils.dmg_extract(IPSWdir + RootFileSystem, TempDir + "RootFS.dmg", VFDecryptKey);
				Utils.ConsoleWriteLine((Utils.GetFileSizeOnDisk(TempDir + "RootFS.dmg") != 0) ? "   [DONE]" : "   [FAILED]", ConsoleColor.DarkGray);
			}
		}

		public static void RemoteModeHome()
		{
			Console.Write("Waiting for KBAGS from server...");
			while (!FileIO.File_Exists(RemoteFileLocation + "iKGD-RemoteServer.plist")) { };
			Console.Write("\nGetting KBAGS...");
			Dictionary<string, object> RemoteServerDict = (Dictionary<string, object>)Plist.readPlist(RemoteFileLocation + "iKGD-RemoteServer.plist");
			Dictionary<string, object> FirmwareInfo = (Dictionary<string, object>)RemoteServerDict["FirmwareInfo"];
			Dictionary<string, object> KBAGS = (Dictionary<string, object>)RemoteServerDict["KBAGS"];
			Device = FirmwareInfo.ContainsKey("Device") ? (string)FirmwareInfo["Device"] : "";
			Firmware = FirmwareInfo.ContainsKey("Firmware") ? (string)FirmwareInfo["Firmware"] : "";
			BuildID = FirmwareInfo.ContainsKey("BuildID") ? (string)FirmwareInfo["BuildID"] : "";
			Platform = FirmwareInfo.ContainsKey("Platform") ? (string)FirmwareInfo["Platform"] : "";
			UpdateRamdiskIsEncrypted = FirmwareInfo.ContainsKey("UpdateRamdiskEncrypted") ? (bool)FirmwareInfo["UpdateRamdiskEncrypted"] : false;
			RestoreRamdiskIsEncrypted = FirmwareInfo.ContainsKey("RestoreRamdiskEncrypted") ? (bool)FirmwareInfo["RestoreRamdiskEncrypted"] : false;
			for (int i = 0; i < images.Length; i++)
			{
				kbags[i] = KBAGS.ContainsKey(images[i]) ? (string)KBAGS[images[i]] : "";
			}
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			Console.WriteLine("Checking resources...");
			Utils.CheckResources();
			MakeDeviceReady();
			GrabKeys();
			Console.Write("Writing keys plist for remote server...");
			Dictionary<string, object> RemoteHomeDict = new Dictionary<string, object>();
			Dictionary<string, object> IVs = new Dictionary<string, object>();
			Dictionary<string, object> Keys = new Dictionary<string, object>();
			for (int i = 0; i < images.Length; i++)
			{
				IVs.Add(images[i], iv[i]);
				Keys.Add(images[i], key[i]);
			}
			RemoteHomeDict.Add("FirmwareInfo", FirmwareInfo);
			RemoteHomeDict.Add("IVs", IVs);
			RemoteHomeDict.Add("Keys", Keys);
			Plist.writeXml(RemoteHomeDict, RemoteFileLocation + "iKGD-RemoteHome.plist");
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			Environment.Exit((int)ExitCode.Success);
		}

		public static void RemoteModeServer()
		{
			FileIO.File_Delete(RemoteFileLocation + "iKGD-RemoteHome.plist");
			FileIO.File_Delete(RemoteFileLocation + "iKGD-RemoteServer.plist");
			Dictionary<string, object> RemoteServerDict = new Dictionary<string, object>();
			Dictionary<string, object> KBAGS = new Dictionary<string, object>();
			Dictionary<string, object> FirmwareInfo = new Dictionary<string, object>();
			FirmwareInfo.Add("Device", Device);
			FirmwareInfo.Add("Firmware", Firmware);
			FirmwareInfo.Add("BuildID", BuildID);
			FirmwareInfo.Add("Platform", Platform);
			FirmwareInfo.Add("UpdateRamdiskEncrypted", UpdateRamdiskIsEncrypted);
			FirmwareInfo.Add("RestoreRamdiskEncrypted", RestoreRamdiskIsEncrypted);
			if (UpdateRamdiskIsEncrypted) KBAGS.Add(images[0], kbags[0]);
			if (RestoreRamdiskIsEncrypted) KBAGS.Add(images[1], kbags[1]);
			for (int i = 2; i < images.Length; i++)
			{
				KBAGS.Add(images[i], kbags[i]);
			}
			RemoteServerDict.Add("FirmwareInfo", FirmwareInfo);
			RemoteServerDict.Add("KBAGS", KBAGS);
			Plist.writeXml(RemoteServerDict, RemoteFileLocation + "iKGD-RemoteServer.plist");
			Console.Write("Waiting for IVs and Keys...");
			while (!FileIO.File_Exists(RemoteFileLocation + "iKGD-RemoteHome.plist")) { };
			Console.Write("\nGetting Keys...");
			Dictionary<string, object> RemoteHomeDict = (Dictionary<string, object>)Plist.readPlist(RemoteFileLocation + "iKGD-RemoteHome.plist");
			Dictionary<string, object> IVs = (Dictionary<string, object>)RemoteHomeDict["IVs"];
			Dictionary<string, object> Keys = (Dictionary<string, object>)RemoteHomeDict["Keys"];
			for (int i = 0; i < images.Length; i++)
			{
				iv[i] = IVs.ContainsKey(images[i]) ? (string)IVs[images[i]] : "";
				key[i] = Keys.ContainsKey(images[i]) ? (string)Keys[images[i]] : "";
			}
			Utils.ConsoleWriteLine("   [DONE]", ConsoleColor.DarkGray);
			FileIO.File_Delete(RemoteFileLocation + "iKGD-RemoteHome.plist");
			FileIO.File_Delete(RemoteFileLocation + "iKGD-RemoteServer.plist");
		}

		private static void PrintUsage(string CurrentProcessName)
		{
			Console.WriteLine();
			Console.WriteLine("Usage: " + CurrentProcessName + " <args> [options]");
			Console.WriteLine("  -i <ipswlocation>     Local path to the iOS firmware");
			Console.WriteLine("  -u <ipswurl>          Remote firmware URL to download files from");
			Console.WriteLine("  -d <device>           Device boardid as n90ap to fetch URL (use with -f)");
			Console.WriteLine("  -f <firmwarebuild>    Firmware build as 9A334 to fetch URL (use with -d)");
			Console.WriteLine("  -k <keysdir>          Path to dir to store keys (default \"{0}\"", KeysDir);
			Console.WriteLine("  -S                    Running on server (also run -H at home)");
			Console.WriteLine("  -H                    Use with -S to get keys from home");
			Console.WriteLine("  -e                    Extract full root filesystem (only with -i)");
			Console.WriteLine("  -r                    Don't reboot device.");
			Console.WriteLine("  -v                    Verbose");
			Console.WriteLine();
			Console.WriteLine(" eg. {0} -ei \"C:\\iPod4,1_5.0_9A334_Restore.ipsw\"", CurrentProcessName);
			Console.WriteLine("     {0} -v -u \"http://apple.com/iPod4,1_5.0_9A334_Restore.ipsw\"", CurrentProcessName);
			Console.WriteLine("     {0} -d iPod4,1 -f 9A334 -v", CurrentProcessName);
			Console.WriteLine();
			Environment.Exit((int)ExitCode.Usage);
		}

		public static void irecv_fbechoikgd()
		{
			Utils.irecovery_fbecho("________________________________________________________________________________________________________");
			Utils.irecovery_fbecho("________________________________________________________________________________________________________");
			Utils.irecovery_fbecho("__________iiii_______KKKKKKKKK____KKKKKKK_____________GGGGGGGGGGGGG_______DDDDDDDDDDDDD_________________");
			Utils.irecovery_fbecho("_________i::::i______K:::::::K____K:::::K__________GGG::::::::::::G_______D::::::::::::DDD______________");
			Utils.irecovery_fbecho("__________iiii_______K:::::::K____K:::::K________GG:::::::::::::::G_______D:::::::::::::::DD____________");
			Utils.irecovery_fbecho("_____________________K:::::::K___K::::::K_______G:::::GGGGGGGG::::G_______DDD:::::DDDDD:::::D___________");
			Utils.irecovery_fbecho("________iiiiiii______KK::::::K__K:::::KKK______G:::::G_______GGGGGG_________D:::::D____D:::::D__________");
			Utils.irecovery_fbecho("________i:::::i________K:::::K_K:::::K________G:::::G_______________________D:::::D_____D:::::D_________");
			Utils.irecovery_fbecho("_________i::::i________K::::::K:::::K_________G:::::G_______________________D:::::D_____D:::::D_________");
			Utils.irecovery_fbecho("_________i::::i________K:::::::::::K__________G:::::G____GGGGGGGGGG_________D:::::D_____D:::::D_________");
			Utils.irecovery_fbecho("_________i::::i________K:::::::::::K__________G:::::G____G::::::::G_________D:::::D_____D:::::D_________");
			Utils.irecovery_fbecho("_________i::::i________K::::::K:::::K_________G:::::G____GGGGG::::G_________D:::::D_____D:::::D_________");
			Utils.irecovery_fbecho("_________i::::i________K:::::K_K:::::K________G:::::G________G::::G_________D:::::D_____D:::::D_________");
			Utils.irecovery_fbecho("_________i::::i______KK::::::K__K:::::KKK______G:::::G_______G::::G_________D:::::D____D:::::D__________");
			Utils.irecovery_fbecho("________i::::::i_____K:::::::K___K::::::K_______G:::::GGGGGGGG::::G_______DDD:::::DDDDD:::::D___________");
			Utils.irecovery_fbecho("________i::::::i_____K:::::::K____K:::::K________GG:::::::::::::::G_______D:::::::::::::::DD____________");
			Utils.irecovery_fbecho("________i::::::i_____K:::::::K____K:::::K__________GGG::::::GGG:::G_______D::::::::::::DDD______________");
			Utils.irecovery_fbecho("________iiiiiiii_____KKKKKKKKK____KKKKKKK_____________GGGGGG___GGGG_______DDDDDDDDDDDDD_________________");
			Utils.irecovery_fbecho("--------------------------------------------------------------------------------------------------------");
			Utils.irecovery_fbecho("--------------------------------------------------------------------------------------------------------");
			Utils.irecovery_fbecho(":: iKGD v" + Version + " initialized!");
			Utils.irecovery_fbecho("-------------------------------------");
			Utils.irecovery_fbecho(":: " + Device + " - iOS " + Firmware + " [" + BuildID + "]");
			Utils.irecovery_fbecho("-------------------------------------");
		}

		enum ExitCode : int
		{
			Success = 0,
			Usage = 1,
			InvalidURL = 2,
			InvalidIPSWLocation = 3,
			IncompatibleDevice = 4,
			PlatformNotSame = 5,
			URLisNotFirmware = 6,
			UnknownError = 10
		}
	}
}