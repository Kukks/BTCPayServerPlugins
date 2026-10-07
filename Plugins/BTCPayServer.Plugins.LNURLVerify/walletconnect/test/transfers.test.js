import { test } from 'node:test'
import assert from 'node:assert/strict'
import { PublicKey } from '@solana/web3.js'
import { getAssociatedTokenAddressSync } from '@solana/spl-token'
import { erc20TransferData, evmTransfer, trc20Trigger, splTransfer } from '../src/transfers.js'

const evmTo = '0x1111111111111111111111111111111111111111'
const amountWord = '0000000000000000000000000000000000000000000000000000000003c6cc00'

test('an ERC-20 transfer encodes transfer(address,uint256)', () => {
  assert.equal(erc20TransferData(evmTo, '63360000'),
    '0xa9059cbb0000000000000000000000001111111111111111111111111111111111111111' + amountWord)
})

test('an EVM transfer goes to the token contract and carries no ether', () => {
  assert.deepEqual(evmTransfer({ from: '0x2222222222222222222222222222222222222222', token: '0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9', recipient: evmTo, baseUnits: '63360000' }), {
    from: '0x2222222222222222222222222222222222222222',
    to: '0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9',
    data: '0xa9059cbb0000000000000000000000001111111111111111111111111111111111111111' + amountWord,
    value: '0x0'
  })
})

test('amounts beyond 2^53 stay exact', () => {
  assert.ok(erc20TransferData(evmTo, '123456789012345678901234567890').endsWith('00000000000000000000000000000000000000018ee90ff6c373e0ee4e3f0ad2'))
})

test('a TRC-20 trigger encodes the recipient account and the amount', () => {
  assert.deepEqual(trc20Trigger({ owner: 'TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t', token: 'TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf', recipient: 'TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7', baseUnits: '63360000' }), {
    owner_address: 'TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t',
    contract_address: 'TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf',
    function_selector: 'transfer(address,uint256)',
    parameter: '00000000000000000000000074472e7d35395a6b5add427eecb7f4b62ad2b071' + amountWord,
    fee_limit: 100000000,
    call_value: 0,
    visible: true
  })
})

const solana = (recipient) => ({
  owner: '9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM',
  recipient,
  mint: '4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU',
  baseUnits: '63360000',
  decimals: 6,
  blockhash: 'EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG'
})

test('an SPL transfer creates the recipient token account if needed and moves the exact amount', () => {
  assert.equal(splTransfer(solana('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v')), 'AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAUIfowIh2C/3h3dzzLBfyCbgkLuUqrxMfrNiNDqLG0LBvK/kLNe/ZX4HpvidTZF/XlYgzH2h4C0f7K4JjqNwRK7dPvFs4UzCvglBFoa4kBrLcyYykeFARISclrXoGeXesFvAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA7RCyzkSFX8TqTPQE0KC0DK1/+zQGi2/G3eQYI3wAup4yXJY9OJInxuz0QKRSODYMLWhOZ2v8QhASOe9jb6fhZxvp6877brTo9ZfNqq8l0MbG75MLS9uDkfKYCA0UvXWEG3fbh12Whk9nL4UbO63msHLSF7V9bN5E6jPWFfv8Aqc5Z21CA/CxtO898qQcS08Ll5sKPJ/Dfu5lTvbCJTAOrAgUGAAEGBAMHAQEHBAIEAQAKDADMxgMAAAAABg==')
})

test('an off-curve recipient is refused instead of nesting a token account', () => {
  const tokenAccount = getAssociatedTokenAddressSync(new PublicKey('4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU'),
    new PublicKey('EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v')).toBase58()
  assert.throws(() => splTransfer(solana(tokenAccount)), { name: 'TokenOwnerOffCurveError' })
})
