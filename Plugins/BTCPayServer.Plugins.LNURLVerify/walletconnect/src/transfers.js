import bs58 from 'bs58'
import { PublicKey, Transaction } from '@solana/web3.js'
import {
  createAssociatedTokenAccountIdempotentInstruction,
  createTransferCheckedInstruction,
  getAssociatedTokenAddressSync
} from '@solana/spl-token'

// Only encoding lives here: recipients, tokens and integer base-unit amounts arrive already validated by the server.
const word = (hex) => hex.padStart(64, '0')
const hex = (bytes) => Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')

export function erc20TransferData (recipient, baseUnits) {
  return '0xa9059cbb' + word(recipient.slice(2).toLowerCase()) + word(BigInt(baseUnits).toString(16))
}

export function evmTransfer ({ from, token, recipient, baseUnits }) {
  return { from, to: token, data: erc20TransferData(recipient, baseUnits), value: '0x0' }
}

export function trc20Trigger ({ owner, token, recipient, baseUnits }) {
  return {
    owner_address: owner,
    contract_address: token,
    function_selector: 'transfer(address,uint256)',
    parameter: word(hex(bs58.decode(recipient).slice(1, 21))) + word(BigInt(baseUnits).toString(16)),
    fee_limit: 100000000,
    call_value: 0,
    visible: true
  }
}

export function splTransfer ({ owner, recipient, mint, baseUnits, decimals, blockhash }) {
  const payer = new PublicKey(owner)
  const to = new PublicKey(recipient)
  const token = new PublicKey(mint)
  // false for the recipient: an off-curve destination fails here instead of funding a nested account the LNURL never watches
  const destination = getAssociatedTokenAddressSync(token, to, false)
  const tx = new Transaction({ feePayer: payer, recentBlockhash: blockhash })
  tx.add(createAssociatedTokenAccountIdempotentInstruction(payer, destination, to, token))
  tx.add(createTransferCheckedInstruction(getAssociatedTokenAddressSync(token, payer, true), token, destination, payer, BigInt(baseUnits), decimals))
  return tx.serialize({ requireAllSignatures: false, verifySignatures: false }).toString('base64')
}
