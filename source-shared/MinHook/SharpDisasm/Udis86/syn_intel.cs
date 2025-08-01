﻿// --------------------------------------------------------------------------------
// SharpDisasm (File: SharpDisasm\syn_intel.cs)
// Copyright (c) 2014-2015 Justin Stenning
// http://spazzarama.com
// https://github.com/spazzarama/SharpDisasm
// https://sharpdisasm.codeplex.com/
//
// SharpDisasm is distributed under the 2-clause "Simplified BSD License".
//
// Portions of SharpDisasm are ported to C# from udis86 a C disassembler project
// also distributed under the terms of the 2-clause "Simplified BSD License" and
// Copyright (c) 2002-2012, Vivek Thampi <vivek.mt@gmail.com>
// All rights reserved.
// UDIS86: https://github.com/vmt/udis86
//
// Redistribution and use in source and binary forms, with or without modification, 
// are permitted provided that the following conditions are met:
// 
// 1. Redistributions of source code must retain the above copyright notice, 
//    this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright notice, 
//    this list of conditions and the following disclaimer in the documentation 
//    and/or other materials provided with the distribution.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND 
// ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED 
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR 
// ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES 
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; 
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON 
// ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT 
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS 
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// --------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace SharpDisasm.Udis86
{
    #pragma warning disable 1591
    public class syn_intel : Syn
    {

        /* -----------------------------------------------------------------------------
         * opr_cast() - Prints an operand cast.
         * -----------------------------------------------------------------------------
         */
        public void opr_cast(ref Ud u, ref ud_operand op)
        {
            if (u.br_far > 0)
            {
                ud_asmprintf(ref u, "far ");
            }
            switch (op.size)
            {
                case 8: ud_asmprintf(ref u, "byte "); break;
                case 16: ud_asmprintf(ref u, "word "); break;
                case 32: ud_asmprintf(ref u, "dword "); break;
                case 64: ud_asmprintf(ref u, "qword "); break;
                case 80: ud_asmprintf(ref u, "tword "); break;
                default: break;
            }
        }

        /* -----------------------------------------------------------------------------
         * gen_operand() - Generates assembly output for each operand.
         * -----------------------------------------------------------------------------
         */
        void gen_operand(ref Ud u, ref ud_operand op, int syn_cast)
        {
            switch (op.type)
            {
                case ud_type.UD_OP_REG:
                    Syn.ud_asmprintf(ref u, "{0}", ud_reg_tab[op.@base - ud_type.UD_R_AL]);
                    break;

                case ud_type.UD_OP_MEM:
                    if (syn_cast > 0)
                    {
                        opr_cast(ref u, ref op);
                    }
                    ud_asmprintf(ref u, "[");
                    if (u.pfx_seg > 0)
                    {
                        Syn.ud_asmprintf(ref u, "{0}:", ud_reg_tab[u.pfx_seg - (int)ud_type.UD_R_AL]);
                    }
                    if (op.@base > 0)
                    {
                        Syn.ud_asmprintf(ref u, "{0}", ud_reg_tab[op.@base - ud_type.UD_R_AL]);
                    }
                    if (op.index > 0)
                    {
                        Syn.ud_asmprintf(ref u, "{0}{1}", op.@base != ud_type.UD_NONE ? "+" : "",
                                                ud_reg_tab[op.index - ud_type.UD_R_AL]);
                        if (op.scale > 0)
                        {
                            Syn.ud_asmprintf(ref u, "*{0}", op.scale);
                        }
                    }
                    if (op.offset != 0)
                    {
                        ud_syn_print_mem_disp(ref u, ref op, (op.@base != ud_type.UD_NONE ||
                                                    op.index != ud_type.UD_NONE) ? 1 : 0);
                    }
                    Syn.ud_asmprintf(ref u, "]");
                    break;

                case ud_type.UD_OP_IMM:
                    ud_syn_print_imm(ref u, ref op);
                    break;


                case ud_type.UD_OP_JIMM:
                    ud_syn_print_addr(ref u, (long)ud_syn_rel_target(ref u, ref op));
                    break;

                case ud_type.UD_OP_PTR:
                    switch (op.size)
                    {
                        case 32:
                            ud_asmprintf(ref u, "word 0x{0:x}:0x{1:x}", op.lval.ptr_seg,
                              op.lval.ptr_off & 0xFFFF);
                            break;
                        case 48:
                            ud_asmprintf(ref u, "dword 0x{0:x}:0x{0:x}", op.lval.ptr_seg,
                              op.lval.ptr_off);
                            break;
                    }
                    break;

                case ud_type.UD_OP_CONST:
                    if (syn_cast > 0) opr_cast(ref u, ref op);
                    ud_asmprintf(ref u, "{0}", op.lval.udword);
                    break;

                default: return;
            }
        }

        /* =============================================================================
         * translates to intel syntax 
         * =============================================================================
         */
        public void ud_translate_intel(ref Ud u)
        {
            /* check if P_OSO prefix is used */
            if (BitOps.P_OSO(u.itab_entry.Prefix) == 0 && u.pfx_opr > 0)
            {
                switch (u.dis_mode)
                {
                    case 16: ud_asmprintf(ref u, "o32 "); break;
                    case 32:
                    case 64: ud_asmprintf(ref u, "o16 "); break;
                }
            }

            /* check if P_ASO prefix was used */
            if (BitOps.P_ASO(u.itab_entry.Prefix) == 0 && u.pfx_adr > 0)
            {
                switch (u.dis_mode)
                {
                    case 16: ud_asmprintf(ref u, "a32 "); break;
                    case 32: ud_asmprintf(ref u, "a16 "); break;
                    case 64: ud_asmprintf(ref u, "a32 "); break;
                }
            }

            if (u.pfx_seg > 0 &&
                u.operand[0].type != ud_type.UD_OP_MEM &&
                u.operand[1].type != ud_type.UD_OP_MEM)
            {
                ud_asmprintf(ref u, "{0} ", ud_reg_tab[u.pfx_seg - (byte)ud_type.UD_R_AL]);
            }

            if (u.pfx_lock > 0)
            {
                ud_asmprintf(ref u, "lock ");
            }
            if (u.pfx_rep > 0)
            {
                ud_asmprintf(ref u, "rep ");
            }
            else if (u.pfx_repe > 0)
            {
                ud_asmprintf(ref u, "repe ");
            }
            else if (u.pfx_repne > 0)
            {
                ud_asmprintf(ref u, "repne ");
            }

            /* print the instruction mnemonic */
            ud_asmprintf(ref u, "{0}", udis86.ud_lookup_mnemonic(u.mnemonic));

            if (u.operand[0].type != ud_type.UD_NONE)
            {
                int cast = 0;
                ud_asmprintf(ref u, " ");
                if (u.operand[0].type == ud_type.UD_OP_MEM)
                {
                    if (u.operand[1].type == ud_type.UD_OP_IMM ||
                        u.operand[1].type == ud_type.UD_OP_CONST ||
                        u.operand[1].type == ud_type.UD_NONE ||
                        (u.operand[0].size != u.operand[1].size &&
                         u.operand[1].type != ud_type.UD_OP_REG))
                    {
                        cast = 1;
                    }
                    else if (u.operand[1].type == ud_type.UD_OP_REG &&
                             u.operand[1].@base == ud_type.UD_R_CL)
                    {
                        switch (u.mnemonic)
                        {
                            case ud_mnemonic_code.UD_Ircl:
                            case ud_mnemonic_code.UD_Irol:
                            case ud_mnemonic_code.UD_Iror:
                            case ud_mnemonic_code.UD_Ircr:
                            case ud_mnemonic_code.UD_Ishl:
                            case ud_mnemonic_code.UD_Ishr:
                            case ud_mnemonic_code.UD_Isar:
                                cast = 1;
                                break;
                            default: break;
                        }
                    }
                }
                gen_operand(ref u, ref u.operand[0], cast);
            }

            if (u.operand[1].type != ud_type.UD_NONE)
            {
                int cast = 0;
                ud_asmprintf(ref u, ", ");
                if (u.operand[1].type == ud_type.UD_OP_MEM &&
                    u.operand[0].size != u.operand[1].size &&
                    !udis86.ud_opr_is_sreg(ref u.operand[0]))
                {
                    cast = 1;
                }
                gen_operand(ref u, ref u.operand[1], cast);
            }

            if (u.operand[2].type != ud_type.UD_NONE)
            {
                int cast = 0;
                ud_asmprintf(ref u, ", ");
                if (u.operand[2].type == ud_type.UD_OP_MEM &&
                    u.operand[2].size != u.operand[1].size)
                {
                    cast = 1;
                }
                gen_operand(ref u, ref u.operand[2], cast);
            }

            if (u.operand[3].type != ud_type.UD_NONE)
            {
                ud_asmprintf(ref u, ", ");
                gen_operand(ref u, ref u.operand[3], 0);
            }
        }
    }
    #pragma warning restore 1591
}
