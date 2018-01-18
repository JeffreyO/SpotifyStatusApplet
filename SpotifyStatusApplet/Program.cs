﻿/*
 * SpotifyStatusApplet 
 *
 * Copyright (c) 2015 Lucas Pardue
 *
 * Licensed to the Apache Software Foundation (ASF) under one
 * or more contributor license agreements.  See the LICENSE file
 * distributed with this work for additional information
 * regarding copyright ownership.  This file is licensed 
 * to you under the Apache License, Version 2.0 (the
 * "License"); you may not use this file except in compliance
 * with the License.  You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied.  See the License for the
 * specific language governing permissions and limitations
 * under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using JariZ;
using System.Threading;
using System.Diagnostics;
using GammaJul.LgLcd;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Runtime.InteropServices;


namespace SpotifyStatusApplet
{
    class SpotifyStatusApplet
    {
        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        private readonly AutoResetEvent m_waitAutoResetEvent = new AutoResetEvent(false);
        // The folowing are declared volatile as they could be written to in event handlers, where the thread belongs to the caller
        private volatile bool m_monoArrived = false;
        private volatile bool m_qvgaArrived = false;
        private volatile bool m_keepRunning = true;
        private volatile bool m_showTitles = false;

        private SpotifyAPI m_spotifyApiInstance;
        private Responses.CFID m_cfid;

        private LcdGraphics m_lcdGraphics;

        SpotifyStatusApplet(bool showTitles)
        {
            m_showTitles = showTitles;
        }

        // Application lifecycle follows the following sequence
        // 1. Acquire the Spotify Local resources
        // 2. Create Notification Tray Icon
        // 3. Block until a suitable device is connected
        // 4. Loop until the application needs to terminate and perform the following
        //    4.1. Test if device handle exists, if not create it, if does the device will need to be reopened
        //    4.2  Test conditions are correct to allow a redraw (applet is enabled by user, device is accessible)
        //    4.4  Obtain the latest Spotify details
        //    4.5  Recreate the graphical elements 
        //    4.6  Call DoUpdateAndDraw
        //    4.7  Invokes delegates for the page updates to retrieve Spotify details and redraw graphics
        //
        // NB the thread sleeps at a rate of roughly 30fps. 
        // This provides a smooth scrolling effect for the scrolling text without overloading system or device resources.
        // Track details update in response to system action (e.g. automatically traversing playlists)
        // or user initiated events (skipping via Spotify application UI or keyboard media keys),
        // pragmatically 
        // Additional tuning could be performed
        [MTAThread]
        internal static void Main(string[] args)
        {
            bool showTitles = true;

            if (args != null && args.Length !=0)
            {
                if (args[0] == "notitles")
                {
                    showTitles = false;
                }
            }

            try
            {
                SpotifyStatusApplet ssa = new SpotifyStatusApplet(showTitles);
                ssa.setupSpotify();

                ssa.setupTrayIcon();

                LcdApplet applet = new LcdApplet("Spotify Status Applet", LcdAppletCapabilities.Both);

                // Register to events to know when a device arrives, then connects the applet to the LCD Manager
                applet.Configure += appletConfigure;
                applet.DeviceArrival += ssa.appletDeviceArrival;
                applet.DeviceRemoval += ssa.appletDeviceRemoval;
                applet.IsEnabledChanged += ssa.appletIsEnabledChanged;
                applet.Connect();

                // We are waiting for the handler thread to warn us for device arrival
                LcdDeviceMonochrome monoDevice = null;
                ssa.m_waitAutoResetEvent.WaitOne();

                do
                {
                    // A monochrome device is connected: creates a monochrome device or reopens an old one
                    if (true == ssa.m_monoArrived)
                    {
                        if (null == monoDevice)
                        {
                            monoDevice = (LcdDeviceMonochrome)applet.OpenDeviceByType(LcdDeviceType.Monochrome);
                            monoDevice.SoftButtonsChanged += ssa.monoDeviceSoftButtonsChanged;
                            ssa.m_lcdGraphics = new LcdGraphics();
                            ssa.m_lcdGraphics.createMonochromeGdiPages(monoDevice);
                            monoDevice.SetAsForegroundApplet = true;
                        }
                        else
                        {
                            monoDevice.ReOpen();
                        }

                        // serviced the last device connection so reset the arrival flag
                        ssa.m_monoArrived = false;
                    }

                    if (ssa.m_qvgaArrived)
                    {
                        // TODO add the implementation as a future project
                        ssa.m_qvgaArrived = false;
                    }
                    
                    // DoUpdateAndDraw only occurs at the framerate specified by LcdPage.DesiredFrameRate, which is 30 by default.
                    if (true == applet.IsEnabled &&
                        null != monoDevice &&
                        false == monoDevice.IsDisposed)
                    {
                        try
                        {
                            ssa.m_lcdGraphics.setMediaPlayerDetails(ssa.getCurrentSpotifyDetails());
                        }
                        catch (System.NullReferenceException)
                        {
                            Debug.WriteLine("GetCurentSpotifyDetails() is NULL. Ad is likely playing.");
                        }

                        ssa.m_lcdGraphics.setShowTitles(ssa.m_showTitles);
                        monoDevice.DoUpdateAndDraw();
                    }

                    Thread.Sleep(33);
                }
                while (true == ssa.m_keepRunning);
            }
            catch (Exception e)
            {
                Trace.WriteLine("Caught exception, application exited " + e.ToString());
            }
        }

        // Attempt to acquire the Spotify resources
        private void setupSpotify()
        {
            m_spotifyApiInstance = new SpotifyAPI(SpotifyAPI.GetOAuth(), "jariz-example.spotilocal.com");
            m_cfid = m_spotifyApiInstance.CFID;

            if (m_cfid.error != null)
            {
                Trace.WriteLine(string.Format("Spotify returned am error {0} (0x{1})", m_cfid.error.message, m_cfid.error.type));
               // Thread.Sleep(-1);
            }
            else
            {
                // it was ok
            }

        }

        // Obtain the current details of the Spotify player, both track info and the player status
        private MediaPlayerDetails getCurrentSpotifyDetails()
        {
            MediaPlayerDetails retVal = new MediaPlayerDetails();
            Responses.Status Current_Status = m_spotifyApiInstance.Status;
            if (m_cfid.error != null)
            {
                Trace.WriteLine(string.Format("Spotify returned a error {0} (0x{1})", m_cfid.error.message, m_cfid.error.type));
                Thread.Sleep(-1);
            }

            if (Current_Status.track != null)
            {
                retVal.currentTrack = Current_Status.track.track_resource.name;
                retVal.currentAlbum = Current_Status.track.album_resource.name;
                retVal.currentArtist = Current_Status.track.artist_resource.name;
                int pos = (int)Current_Status.playing_position;
                int len = Current_Status.track.length;
                retVal.playTime = String.Format("{0}:{1:D2}/{2}:{3:D2}", pos / 60, pos % 60, len / 60, len % 60);
            }

            retVal.playing = Current_Status.playing;
            retVal.online = Current_Status.online;
            retVal.privateSession = Current_Status.open_graph_state.private_session;

            return retVal;
        }

        // Launch the thread that manages the notification tray icon
        private void setupTrayIcon()
        {
            var myThread = new Thread(delegate()
            {
                using (AppTrayIcon ati = new AppTrayIcon(this))
                {
                    ati.Display();
                    Application.Run();
                }
            });

            myThread.SetApartmentState(ApartmentState.STA);
            myThread.Start();
        }

        // Event handler for new device arrival in the system.
        // Monochrome devices include (G510, G13, G15, Z10)
        private void appletDeviceArrival(object sender, LcdDeviceTypeEventArgs e)
        {            
            switch (e.DeviceType)
            {
                // A monochrome device (G13/G15/Z10) was connected
                case LcdDeviceType.Monochrome:
                    m_monoArrived = true;
                    break;
                case LcdDeviceType.Qvga:
                    m_qvgaArrived = true;
                    break;
                default:
                    break;
            }

            m_waitAutoResetEvent.Set();
        }       

        /////////////////////////////////////////////
        // Unused functions at this time
        //////////////////////////////////////////////

        /// Event handler for Configure button click (for this applet) in the LCD Manager.
        private static void appletConfigure(object sender, EventArgs e)
        {
            // No action required
        }

        // Event handler for device removal
        private void appletDeviceRemoval(object sender, LcdDeviceTypeEventArgs e)
        {
            // No action required
        }

        // Event handler for applet enablement or disablement in the LCD Manager
        private void appletIsEnabledChanged(object sender, EventArgs e)
        {
            // No action required
        }

        /// This event handler is called whenever the soft buttons are pressed or released.

        private void monoDeviceSoftButtonsChanged(object sender, LcdSoftButtonsEventArgs e)
        {
            LcdDevice device = (LcdDevice)sender;

            // First button 
            if ((e.SoftButtons & LcdSoftButtons.Button0) == LcdSoftButtons.Button0)
            {
                m_showTitles = !m_showTitles;
                
            }

            // Second button 
            if ((e.SoftButtons & LcdSoftButtons.Button1) == LcdSoftButtons.Button1)
            {
                keybd_event(Convert.ToByte(Keys.MediaPreviousTrack), 0, 0x00, 0); //KEYDOWN PrevTrack Key
                keybd_event(Convert.ToByte(Keys.MediaPreviousTrack), 0, 0x02, 0); //KEYUP PrevTrack Key
            }

            // Third button 
            if ((e.SoftButtons & LcdSoftButtons.Button2) == LcdSoftButtons.Button2)
            {
                keybd_event(Convert.ToByte(Keys.MediaPlayPause), 0, 0x00, 0); //KEYDOWN PlayPause Key
                keybd_event(Convert.ToByte(Keys.MediaPlayPause), 0, 0x02, 0); //KEYUP PlayPause Key

            }

            // Fourth button 
            if ((e.SoftButtons & LcdSoftButtons.Button3) == LcdSoftButtons.Button3)
            {
                keybd_event(Convert.ToByte(Keys.MediaNextTrack), 0, 0x00, 0); //KEYDOWN NextTrack Key
                keybd_event(Convert.ToByte(Keys.MediaNextTrack), 0, 0x02, 0); //KEYUP NextTrack Key
                //m_keepRunning = false;
            }
        }

        public void Terminate()
        {
            m_keepRunning = false;
        }
    }
}
