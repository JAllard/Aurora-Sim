/*
 * Copyright (c) Contributors, http://aurora-sim.org/, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using OpenMetaverse;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    /// <summary>
    ///   Special collection that is optimized for tracking unacknowledged packets
    /// </summary>
    public sealed class UnackedPacketCollection
    {
        /// <summary>
        ///   Holds the actual unacked packet data, sorted by sequence number
        /// </summary>
        private readonly Dictionary<uint, OutgoingPacket> m_packets = new Dictionary<uint, OutgoingPacket>();

        /// <summary>
        ///   Holds information about pending acknowledgements
        /// </summary>
        private readonly ConcurrentQueue<PendingAck> m_pendingAcknowledgements = new ConcurrentQueue<PendingAck>();

        /// <summary>
        ///   Holds packets that need to be added to the unacknowledged list
        /// </summary>
        private readonly ConcurrentQueue<OutgoingPacket> m_pendingAdds = new ConcurrentQueue<OutgoingPacket>();

        /// <summary>
        ///   Holds information about pending removals
        /// </summary>
        private readonly ConcurrentQueue<uint> m_pendingRemoves = new ConcurrentQueue<uint>();

        /// <summary>
        ///   Add an unacked packet to the collection
        /// </summary>
        /// <param name = "packet">Packet that is awaiting acknowledgement</param>
        /// <returns>True if the packet was successfully added, false if the
        ///   packet already existed in the collection</returns>
        /// <remarks>
        ///   This does not immediately add the ACK to the collection,
        ///   it only queues it so it can be added in a thread-safe way later
        /// </remarks>
        public void Add(OutgoingPacket packet)
        {
            m_pendingAdds.Enqueue(packet);
        }

        /// <summary>
        ///   Marks a packet as acknowledged
        ///   This method is used when an acknowledgement is received from the network for a previously
        ///   sent packet. Effects of removal this way are to update unacked byte count, adjust RTT
        ///   and increase throttle to the coresponding client.
        /// </summary>
        /// <param name = "sequenceNumber">Sequence number of the packet to
        ///   acknowledge</param>
        /// <param name = "currentTime">Current value of Environment.TickCount</param>
        /// <param name="fromResend"></param>
        /// <remarks>
        ///   This does not immediately acknowledge the packet, it only
        ///   queues the ack so it can be handled in a thread-safe way later
        /// </remarks>
        public void Acknowledge(uint sequenceNumber, int currentTime, bool fromResend)
        {
            m_pendingAcknowledgements.Enqueue(new PendingAck(sequenceNumber, currentTime, fromResend));
        }

        /// <summary>
        ///   Marks a packet as no longer needing acknowledgement without a received acknowledgement.
        ///   This method is called when a packet expires and we no longer need an acknowledgement.
        ///   When some reliable packet types expire, they are handled in a way other than simply
        ///   resending them. The only effect of removal this way is to update unacked byte count.
        /// </summary>
        /// <param name = "sequenceNumber">Sequence number of the packet to
        ///   acknowledge</param>
        /// <remarks>
        ///   The does not immediately remove the packet, it only queues the removal
        ///   so it can be handled in a thread safe way later
        /// </remarks>
        public void Remove(uint sequenceNumber)
        {
            m_pendingRemoves.Enqueue(sequenceNumber);
        }

        /// <summary>
        ///   Returns a list of all of the packets with a TickCount older than
        ///   the specified timeout
        /// </summary>
        /// <param name = "timeoutMS">Number of ticks (milliseconds) before a
        ///   packet is considered expired</param>
        /// <returns>A list of all expired packets according to the given
        ///   expiration timeout</returns>
        /// <remarks>
        ///   This function is not thread safe, and cannot be called
        ///   multiple times concurrently
        /// </remarks>
        public List<OutgoingPacket> GetExpiredPackets(int timeoutMS)
        {
            ProcessQueues();

            List<OutgoingPacket> expiredPackets = null;
            //Note:This is never used
            int expiredPacketsBytes = 0;

            if (m_packets.Count > 0)
            {
                int now = Environment.TickCount & Int32.MaxValue;
                int i = 0;
#if (!ISWIN)
                foreach (OutgoingPacket packet in m_packets.Values)
                {
                    if (packet.TickCount != 0)
                    {
                        if (now - packet.TickCount >= timeoutMS)
                        {
                            if (expiredPackets == null)
                                expiredPackets = new List<OutgoingPacket>();

                            // The TickCount will be set to the current time when the packet
                            // is actually sent out again
                            packet.TickCount = 0;

                            expiredPackets.Add(packet);
                            expiredPacketsBytes += packet.Buffer.DataLength;
                            if (i++ > 50) // limit number of packets loop
                                break;
                        }
                    }
                }
#else
                foreach (OutgoingPacket packet in m_packets.Values.Where(packet => packet.TickCount != 0).Where(packet => now - packet.TickCount >= timeoutMS))
                {
                    if (expiredPackets == null)
                        expiredPackets = new List<OutgoingPacket>();

                    // The TickCount will be set to the current time when the packet
                    // is actually sent out again
                    packet.TickCount = 0;

                    expiredPackets.Add(packet);
                    expiredPacketsBytes += packet.Buffer.DataLength;
                    if (i++ > 50) // limit number of packets loop
                        break;
                }
#endif
            }

            if (expiredPackets != null && expiredPackets.Count > 0)
            {
                // As with other network applications, assume that an expired packet is
                // an indication of some network problem, slow transmission
                expiredPackets[0].Client.SlowDownSend();
            }

            return expiredPackets;
        }

        private void ProcessQueues()
        {
            // Process all the pending adds

            if (m_pendingAdds != null)
            {
                OutgoingPacket pendingAdd;
                while (m_pendingAdds.TryDequeue(out pendingAdd))
                {
                    if (pendingAdd != null && m_packets != null)
                    {
                        m_packets[pendingAdd.SequenceNumber] = pendingAdd;
                    }
                }
            }

            // Process all the pending removes, including updating statistics and round-trip times
            PendingAck pendingAcknowledgement;
            while (m_pendingAcknowledgements.TryDequeue(out pendingAcknowledgement))
            {
                OutgoingPacket ackedPacket;
                if (m_packets != null && m_packets.TryGetValue(pendingAcknowledgement.SequenceNumber, out ackedPacket))
                {
                    if (ackedPacket != null)
                    {
                        m_packets.Remove(pendingAcknowledgement.SequenceNumber);

                        // Update stats
                        Interlocked.Add(ref ackedPacket.Client.UnackedBytes, -ackedPacket.Buffer.DataLength);

                        if (!pendingAcknowledgement.FromResend)
                        {
                            // Calculate the round-trip time for this packet and its ACK
                            int rtt = pendingAcknowledgement.RemoveTime - ackedPacket.TickCount;
                            if (rtt > 0)
                                ackedPacket.Client.UpdateRoundTrip(rtt);
                        }
                    }
                }
            }

            uint pendingRemove;
            while (m_pendingRemoves.TryDequeue(out pendingRemove))
            {
                OutgoingPacket removedPacket;
                if (m_packets != null && m_packets.TryGetValue(pendingRemove, out removedPacket))
                {
                    if (removedPacket != null)
                    {
                        m_packets.Remove(pendingRemove);

                        // Update stats
                        Interlocked.Add(ref removedPacket.Client.UnackedBytes, -removedPacket.Buffer.DataLength);
                        removedPacket.Destroy(3);
                    }
                }
            }
        }

        #region Nested type: PendingAck

        /// <summary>
        ///   Holds information about a pending acknowledgement
        /// </summary>
        private struct PendingAck
        {
            /// <summary>
            ///   Whether or not this acknowledgement was attached to a
            ///   resent packet. If so, round-trip time will not be calculated
            /// </summary>
            public readonly bool FromResend;

            /// <summary>
            ///   Environment.TickCount value when the remove was queued.
            ///   This is used to update round-trip times for packets
            /// </summary>
            public readonly int RemoveTime;

            /// <summary>
            ///   Sequence number of the packet to remove
            /// </summary>
            public readonly uint SequenceNumber;

            public PendingAck(uint sequenceNumber, int currentTime, bool fromResend)
            {
                SequenceNumber = sequenceNumber;
                RemoveTime = currentTime;
                FromResend = fromResend;
            }
        }

        #endregion
    }
}