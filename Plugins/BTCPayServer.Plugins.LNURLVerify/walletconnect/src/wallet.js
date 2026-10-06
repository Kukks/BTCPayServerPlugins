import { UniversalConnector } from '@reown/appkit-universal-connector'
import { evmTransfer, splTransfer, trc20Trigger } from './transfers.js'

const methods = { eip155: 'eth_sendTransaction', solana: 'solana_signAndSendTransaction', tron: 'tron_signTransaction' }
const natives = { eip155: ['Ether', 'ETH', 18], solana: ['Solana', 'SOL', 9], tron: ['Tron', 'TRX', 6] }
const tronApis = { 'tron:0x2b6653dc': 'https://api.trongrid.io', 'tron:0xcd8690dc': 'https://nile.trongrid.io', 'tron:0x94a9059e': 'https://api.shasta.trongrid.io' }
let connector = null

const rpc = (chain, projectId) => 'https://rpc.walletconnect.org/v1/?chainId=' + encodeURIComponent(chain) + '&projectId=' + encodeURIComponent(projectId)

function caipNetwork (network, projectId) {
  const [name, symbol, decimals] = natives[network.namespace]
  return {
    id: network.chain.slice(network.namespace.length + 1),
    chainNamespace: network.namespace,
    caipNetworkId: network.chain,
    name: network.label,
    nativeCurrency: { name, symbol, decimals },
    rpcUrls: { default: { http: [rpc(network.chain, projectId)] } }
  }
}

async function connect (projectId, networks) {
  if (!connector) {
    const byNamespace = {}
    for (const n of networks) (byNamespace[n.namespace] ??= []).push(caipNetwork(n, projectId))
    connector = await UniversalConnector.init({
      projectId,
      metadata: { name: document.title, description: 'Invoice checkout', url: location.origin, icons: [] },
      networks: Object.entries(byNamespace).map(([namespace, chains]) => ({ namespace, chains, methods: [methods[namespace]], events: [] }))
    })
  }
  return connector
}

function account (chain) {
  const accounts = connector.provider.session?.namespaces[chain.split(':')[0]]?.accounts ?? []
  const match = accounts.find((a) => a.startsWith(chain + ':'))
  return match ? match.slice(chain.length + 1) : null
}

async function post (url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  if (!res.ok) throw new Error(url + ' answered HTTP ' + res.status)
  return res.json()
}

export async function pay ({ projectId, network, networks }) {
  const wallet = await connect(projectId, networks)
  let from = account(network.chain)
  if (!from) {
    if (wallet.provider.session) await wallet.disconnect()
    await wallet.connect()
    from = account(network.chain)
  }
  if (!from) throw new Error('The connected wallet has no account on ' + network.label)
  const transfer = { from, owner: from, token: network.token, recipient: network.destination, baseUnits: network.baseUnits }
  if (network.namespace === 'eip155')
    return await wallet.request({ method: 'eth_sendTransaction', params: [evmTransfer(transfer)] }, network.chain)
  if (network.namespace === 'solana') {
    const latest = await post(rpc(network.chain, projectId), { jsonrpc: '2.0', id: 1, method: 'getLatestBlockhash', params: [{ commitment: 'finalized' }] })
    const transaction = splTransfer({ ...transfer, mint: network.token, decimals: network.decimals, blockhash: latest.result.value.blockhash })
    return (await wallet.request({ method: 'solana_signAndSendTransaction', params: { transaction } }, network.chain)).signature
  }
  const api = tronApis[network.chain]
  if (!api) throw new Error('No Tron node is known for ' + network.label)
  const built = await post(api + '/wallet/triggersmartcontract', trc20Trigger(transfer))
  if (!built.transaction) throw new Error('Tron could not build the transfer')
  const signed = await wallet.request({ method: 'tron_signTransaction', params: { address: from, transaction: { transaction: built.transaction } } }, network.chain)
  const tx = signed.result ?? signed
  const sent = await post(api + '/wallet/broadcasttransaction', tx)
  if (!sent.result) throw new Error('Tron refused the transfer: ' + (sent.message ?? sent.code))
  return tx.txID
}
